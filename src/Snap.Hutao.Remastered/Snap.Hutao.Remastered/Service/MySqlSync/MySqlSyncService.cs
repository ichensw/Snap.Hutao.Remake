// Copyright (c) DGP Studio. All rights reserved.
// Licensed under the MIT license.

using MySqlConnector;
using Snap.Hutao.Remastered.Core.Text.Json;
using Snap.Hutao.Remastered.Model.Entity;
using Snap.Hutao.Remastered.Model.Intrinsic;
using Snap.Hutao.Remastered.Model.Metadata;
using Snap.Hutao.Remastered.Model.Metadata.Item;
using Snap.Hutao.Remastered.Model.Metadata.Reliquary;
using Snap.Hutao.Remastered.Service.AvatarInfo.Factory;
using Snap.Hutao.Remastered.Service.Metadata;
using Snap.Hutao.Remastered.Service.Metadata.ContextAbstraction;
using Snap.Hutao.Remastered.Service.Notification;
using Snap.Hutao.Remastered.Web.Endpoint.Hutao;
using Snap.Hutao.Remastered.Web.Hoyolab;
using Snap.Hutao.Remastered.Web.Hoyolab.Takumi.GameRecord.Avatar;
using System.Collections.Immutable;
using System.Collections.Frozen;
using System.IO;
using AvatarIds = Snap.Hutao.Remastered.Model.Metadata.Avatar.AvatarIds;
using DailyNoteExpedition = Snap.Hutao.Remastered.Web.Hoyolab.Takumi.GameRecord.DailyNote.Expedition;
using EntityAvatarInfo = Snap.Hutao.Remastered.Model.Entity.AvatarInfo;
using MetaAvatar = Snap.Hutao.Remastered.Model.Metadata.Avatar.Avatar;
using MetaMonster = Snap.Hutao.Remastered.Model.Metadata.Monster.Monster;
using MetaReliquary = Snap.Hutao.Remastered.Model.Metadata.Reliquary.Reliquary;
using MetaReliquarySet = Snap.Hutao.Remastered.Model.Metadata.Reliquary.ReliquarySet;
using MetaSkill = Snap.Hutao.Remastered.Model.Metadata.Avatar.Skill;
using MetaWeapon = Snap.Hutao.Remastered.Model.Metadata.Weapon.Weapon;
using WebReliquary = Snap.Hutao.Remastered.Web.Hoyolab.Takumi.GameRecord.Avatar.Reliquary;
using WebReliquaryProperty = Snap.Hutao.Remastered.Web.Hoyolab.Takumi.GameRecord.Avatar.ReliquaryProperty;
using WebGachaType = Snap.Hutao.Remastered.Web.Hoyolab.Hk4e.Event.GachaInfo.GachaType;

namespace Snap.Hutao.Remastered.Service.MySqlSync;

[Service(ServiceLifetime.Singleton)]
public sealed class MySqlSyncService
{
    private const string MetadataSyncSchemaVersion = "metadata-sync-v4-wiki-monster-key";
    private const string MetadataSyncSource = "SnapHutaoMetadata";

    private readonly ILogger<MySqlSyncService> logger;
    private readonly IMetadataService metadataService;
    private readonly IMessenger messenger;
    private readonly ITaskContext taskContext;
    private Task? metadataSyncTask;
    private bool metadataSynced;

    private readonly record struct MetadataTableSyncState(string TableName, string Lang, string ContentHash, long RowCount);

    private readonly record struct AvatarScoreSnapshot(
        uint AvatarId,
        double TotalScore,
        string RecommendedSubPropertiesJson,
        ImmutableArray<ReliquaryScoreSnapshot> Reliquaries);

    private readonly record struct ReliquaryScoreSnapshot(
        int EquipPosition,
        uint ReliquaryId,
        double Score,
        string ScoredSubPropertiesJson);

    public MySqlSyncService(ILogger<MySqlSyncService> logger, IMetadataService metadataService, IMessenger messenger, ITaskContext taskContext)
    {
        this.logger = logger;
        this.metadataService = metadataService;
        this.messenger = messenger;
        this.taskContext = taskContext;
    }

    public async ValueTask SyncAvatarInfosAsync(string uid, IEnumerable<EntityAvatarInfo> avatarInfos, CancellationToken token = default)
    {
        TraceDebug($"SyncAvatarInfosAsync enter uid={uid}");
        ImmutableArray<EntityAvatarInfo> avatarInfoArray = [.. avatarInfos];
        IReadOnlyDictionary<uint, AvatarScoreSnapshot> scoreSnapshots = await CreateAvatarScoreSnapshotsAsync(avatarInfoArray, token).ConfigureAwait(false);

        await ExecuteAsync("avatars", async connection =>
        {
            await EnsureAvatarScoreTablesAsync(connection, token).ConfigureAwait(false);
            await UpsertAccountAsync(connection, uid, default, token).ConfigureAwait(false);
            await ExecuteNonQueryAsync(connection, "DELETE FROM hutao_avatar_relics WHERE uid=@uid", token, ("@uid", uid)).ConfigureAwait(false);
            await ExecuteNonQueryAsync(connection, "DELETE FROM hutao_avatar_skills WHERE uid=@uid", token, ("@uid", uid)).ConfigureAwait(false);
            await ExecuteNonQueryAsync(connection, "DELETE FROM hutao_avatar_constellations WHERE uid=@uid", token, ("@uid", uid)).ConfigureAwait(false);
            await ExecuteNonQueryAsync(connection, "DELETE FROM hutao_avatar_relic_scores WHERE uid=@uid", token, ("@uid", uid)).ConfigureAwait(false);

            foreach (EntityAvatarInfo info in avatarInfos)
            {
                if (info.Info2 is not { } detail)
                {
                    continue;
                }

                Character avatar = detail.Base;
                uint avatarId = (uint)avatar.Id;
                await ExecuteNonQueryAsync(
                    connection,
                    """
                    INSERT INTO hutao_avatars
                    (uid, avatar_id, name, element, level, rarity, fetter, constellation_num, promote_level, weapon_id, weapon_level, weapon_affix_level, raw_json)
                    VALUES
                    (@uid, @avatar_id, @name, @element, @level, @rarity, @fetter, @constellation_num, @promote_level, @weapon_id, @weapon_level, @weapon_affix_level, @raw_json)
                    ON DUPLICATE KEY UPDATE
                    name=VALUES(name), element=VALUES(element), level=VALUES(level), rarity=VALUES(rarity), fetter=VALUES(fetter),
                    constellation_num=VALUES(constellation_num), promote_level=VALUES(promote_level), weapon_id=VALUES(weapon_id),
                    weapon_level=VALUES(weapon_level), weapon_affix_level=VALUES(weapon_affix_level), raw_json=VALUES(raw_json),
                    synced_at=CURRENT_TIMESTAMP
                    """,
                    token,
                    ("@uid", uid),
                    ("@avatar_id", avatarId),
                    ("@name", avatar.Name),
                    ("@element", avatar.Element.ToString()),
                    ("@level", (uint)avatar.Level),
                    ("@rarity", (int)avatar.Rarity),
                    ("@fetter", (uint)avatar.Fetter),
                    ("@constellation_num", avatar.ActivedConstellationNum),
                    ("@promote_level", (uint)avatar.PromoteLevel),
                    ("@weapon_id", (uint)detail.Weapon.Id),
                    ("@weapon_level", (uint)detail.Weapon.Level),
                    ("@weapon_affix_level", detail.Weapon.AffixLevel),
                    ("@raw_json", JsonSerializer.Serialize(detail, JsonOptions.Default))).ConfigureAwait(false);

                foreach (WebReliquary relic in detail.Relics)
                {
                    await ExecuteNonQueryAsync(
                        connection,
                        """
                        INSERT INTO hutao_avatar_relics
                        (uid, avatar_id, equip_pos, reliquary_id, name, rarity, level, set_id, set_name, main_property_type, main_property_value, sub_properties_json, raw_json)
                        VALUES
                        (@uid, @avatar_id, @equip_pos, @reliquary_id, @name, @rarity, @level, @set_id, @set_name, @main_property_type, @main_property_value, @sub_properties_json, @raw_json)
                        """,
                        token,
                        ("@uid", uid),
                        ("@avatar_id", avatarId),
                        ("@equip_pos", (int)relic.Position),
                        ("@reliquary_id", (uint)relic.Id),
                        ("@name", relic.Name),
                        ("@rarity", (int)relic.Rarity),
                        ("@level", relic.Level),
                        ("@set_id", (uint)relic.ReliquarySet.Id),
                        ("@set_name", relic.ReliquarySet.Name),
                        ("@main_property_type", (int)relic.MainProperty.PropertyType),
                        ("@main_property_value", relic.MainProperty.Value),
                        ("@sub_properties_json", JsonSerializer.Serialize(relic.SubPropertyList, JsonOptions.Default)),
                        ("@raw_json", JsonSerializer.Serialize(relic, JsonOptions.Default))).ConfigureAwait(false);
                }

                foreach (Skill skill in detail.Skills)
                {
                    await ExecuteNonQueryAsync(
                        connection,
                        """
                        INSERT INTO hutao_avatar_skills (uid, avatar_id, skill_id, skill_type, level, raw_json)
                        VALUES (@uid, @avatar_id, @skill_id, @skill_type, @level, @raw_json)
                        """,
                        token,
                        ("@uid", uid),
                        ("@avatar_id", avatarId),
                        ("@skill_id", (uint)skill.SkillId),
                        ("@skill_type", (uint)skill.SkillType),
                        ("@level", (uint)skill.Level),
                        ("@raw_json", JsonSerializer.Serialize(skill, JsonOptions.Default))).ConfigureAwait(false);
                }

                foreach (Constellation constellation in detail.Constellations)
                {
                    await ExecuteNonQueryAsync(
                        connection,
                        """
                        INSERT INTO hutao_avatar_constellations (uid, avatar_id, position, skill_id, name, is_actived, raw_json)
                        VALUES (@uid, @avatar_id, @position, @skill_id, @name, @is_actived, @raw_json)
                        """,
                        token,
                        ("@uid", uid),
                        ("@avatar_id", avatarId),
                        ("@position", constellation.Position),
                        ("@skill_id", (uint)constellation.Id),
                        ("@name", constellation.Name),
                        ("@is_actived", constellation.IsActived),
                        ("@raw_json", JsonSerializer.Serialize(constellation, JsonOptions.Default))).ConfigureAwait(false);
                }

                if (scoreSnapshots.TryGetValue(avatarId, out AvatarScoreSnapshot scoreSnapshot))
                {
                    await UpsertAvatarScoreAsync(connection, uid, scoreSnapshot, token).ConfigureAwait(false);
                }
            }
        }, token).ConfigureAwait(false);
    }

    public async ValueTask SyncBackpackAsync(string uid, BackpackArchive archive, IEnumerable<BackpackItem> items, CancellationToken token = default)
    {
        TraceDebug($"SyncBackpackAsync enter uid={uid} archive={archive.InnerId}");

        await ExecuteAsync("backpack", async connection =>
        {
            await UpsertAccountAsync(connection, uid, default, token).ConfigureAwait(false);
            await ExecuteNonQueryAsync(
                connection,
                """
                INSERT INTO hutao_backpack_archives (local_archive_id, uid, name, is_selected)
                VALUES (@local_archive_id, @uid, @name, @is_selected)
                ON DUPLICATE KEY UPDATE uid=VALUES(uid), name=VALUES(name), is_selected=VALUES(is_selected), synced_at=CURRENT_TIMESTAMP
                """,
                token,
                ("@local_archive_id", archive.InnerId.ToString()),
                ("@uid", uid),
                ("@name", archive.Name),
                ("@is_selected", archive.IsSelected)).ConfigureAwait(false);

            await ExecuteNonQueryAsync(connection, "DELETE FROM hutao_backpack_items WHERE local_archive_id=@archive_id", token, ("@archive_id", archive.InnerId.ToString())).ConfigureAwait(false);

            foreach (BackpackItem item in items)
            {
                await ExecuteNonQueryAsync(
                    connection,
                    """
                    INSERT INTO hutao_backpack_items
                    (uid, local_archive_id, item_id, item_guid, count, level, promote_level, refinement_rank, main_prop_id, append_prop_ids_json, is_locked, is_marked)
                    VALUES
                    (@uid, @local_archive_id, @item_id, @item_guid, @count, @level, @promote_level, @refinement_rank, @main_prop_id, @append_prop_ids_json, @is_locked, @is_marked)
                    """,
                    token,
                    ("@uid", uid),
                    ("@local_archive_id", archive.InnerId.ToString()),
                    ("@item_id", item.ItemId),
                    ("@item_guid", item.Guid),
                    ("@count", item.Count),
                    ("@level", item.Level),
                    ("@promote_level", item.PromoteLevel),
                    ("@refinement_rank", item.RefinementRank),
                    ("@main_prop_id", item.MainPropId),
                    ("@append_prop_ids_json", item.AppendPropIdListJson),
                    ("@is_locked", item.IsLocked),
                    ("@is_marked", item.IsMarked)).ConfigureAwait(false);
            }
        }, token).ConfigureAwait(false);
    }

    private async ValueTask<IReadOnlyDictionary<uint, AvatarScoreSnapshot>> CreateAvatarScoreSnapshotsAsync(ImmutableArray<EntityAvatarInfo> avatarInfos, CancellationToken token)
    {
        if (avatarInfos.IsDefaultOrEmpty)
        {
            return FrozenDictionary<uint, AvatarScoreSnapshot>.Empty;
        }

        if (!await metadataService.InitializeAsync().ConfigureAwait(false))
        {
            TraceDebug("avatars: skip score snapshots because metadata initialization failed");
            return FrozenDictionary<uint, AvatarScoreSnapshot>.Empty;
        }

        try
        {
            SummaryFactoryMetadataContext context = await metadataService.GetContextAsync<SummaryFactoryMetadataContext>(token).ConfigureAwait(false);
            Dictionary<uint, AvatarScoreSnapshot> snapshots = [];

            foreach (EntityAvatarInfo info in avatarInfos)
            {
                if (info.Info2 is not { } detail)
                {
                    continue;
                }

                if (TryCreateAvatarScoreSnapshot(context, detail, out AvatarScoreSnapshot snapshot))
                {
                    snapshots[snapshot.AvatarId] = snapshot;
                }
            }

            TraceDebug($"avatars: score snapshots created count={snapshots.Count}");
            return snapshots;
        }
        catch (Exception ex)
        {
            TraceDebug($"avatars: score snapshots failed {ex.GetType().Name}: {ex.Message}");
            logger.LogWarning(ex, "Failed to calculate avatar score snapshots for MySQL");
            return FrozenDictionary<uint, AvatarScoreSnapshot>.Empty;
        }
    }

    private async ValueTask BackfillAvatarScoresFromExistingRowsAsync(MySqlConnection connection, CancellationToken token)
    {
        await EnsureAvatarScoreTablesAsync(connection, token).ConfigureAwait(false);

        long missingCount = await ExecuteScalarAsync(
            connection,
            """
            SELECT COUNT(*)
            FROM hutao_avatars a
            LEFT JOIN hutao_avatar_scores s ON s.uid = a.uid AND s.avatar_id = a.avatar_id
            WHERE s.avatar_id IS NULL
            """,
            token).ConfigureAwait(false);
        if (missingCount is 0)
        {
            TraceDebug("avatar-score: backfill skipped because scores are complete");
            return;
        }

        SummaryFactoryMetadataContext context = await metadataService.GetContextAsync<SummaryFactoryMetadataContext>(token).ConfigureAwait(false);
        List<(string Uid, AvatarScoreSnapshot Snapshot)> snapshots = [];

        await using (MySqlCommand command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT a.uid, a.raw_json
                FROM hutao_avatars a
                LEFT JOIN hutao_avatar_scores s ON s.uid = a.uid AND s.avatar_id = a.avatar_id
                WHERE s.avatar_id IS NULL
                """;

            await using MySqlDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                string uid = reader.GetString(0);
                string rawJson = reader.GetString(1);

                try
                {
                    DetailedCharacter? detail = JsonSerializer.Deserialize<DetailedCharacter>(rawJson, JsonOptions.Default);
                    if (detail is not null && TryCreateAvatarScoreSnapshot(context, detail, out AvatarScoreSnapshot snapshot))
                    {
                        snapshots.Add((uid, snapshot));
                    }
                }
                catch (Exception ex) when (ex is JsonException or NotSupportedException)
                {
                    TraceDebug($"avatar-score: skip malformed raw_json uid={uid} {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        foreach ((string uid, AvatarScoreSnapshot snapshot) in snapshots)
        {
            await UpsertAvatarScoreAsync(connection, uid, snapshot, token).ConfigureAwait(false);
        }

        TraceDebug($"avatar-score: backfilled {snapshots.Count}/{missingCount}");
    }

    private static bool TryCreateAvatarScoreSnapshot(SummaryFactoryMetadataContext context, DetailedCharacter detail, out AvatarScoreSnapshot snapshot)
    {
        snapshot = default;

        if (AvatarIds.IsPlayer(detail.Base.Id))
        {
            return false;
        }

        MetaAvatar metaAvatar = context.GetAvatar(detail.Base.Id);
        ImmutableArray<FightProperty> recommendedSubProperties = detail.RecommendRelicProperty.RecommendProperties.SubPropertyList;
        EnergyType energyType = metaAvatar.SkillDepot.EnergySkill.SpecialEnergyType;
        bool isCritEffective = AvatarIds.IsCritEffective(metaAvatar.Id);
        List<ReliquaryScoreSnapshot> reliquaries = [];
        double totalScore = 0;

        foreach (WebReliquary relic in detail.Relics)
        {
            double score = ReliquaryScoreCalculator.Calculate(recommendedSubProperties, relic.SubPropertyList, energyType, isCritEffective);
            totalScore += score;
            reliquaries.Add(new(
                (int)relic.Position,
                (uint)relic.Id,
                score,
                SerializeScoredSubProperties(recommendedSubProperties, relic.SubPropertyList, energyType, isCritEffective)));
        }

        snapshot = new(
            (uint)detail.Base.Id,
            totalScore,
            JsonSerializer.Serialize(recommendedSubProperties, JsonOptions.Default),
            [.. reliquaries]);
        return true;
    }

    private static async ValueTask EnsureAvatarScoreTablesAsync(MySqlConnection connection, CancellationToken token)
    {
        await ExecuteNonQueryAsync(
            connection,
            """
            CREATE TABLE IF NOT EXISTS hutao_avatar_scores (
              uid VARCHAR(32) NOT NULL COMMENT '游戏 UID',
              avatar_id BIGINT UNSIGNED NOT NULL COMMENT '角色 ID',
              total_score DECIMAL(10,4) NOT NULL COMMENT '角色圣遗物总评分，使用胡桃我的角色页同款算法',
              score_algorithm VARCHAR(64) NOT NULL COMMENT '评分算法版本',
              recommended_sub_properties_json JSON NULL COMMENT '米游社推荐副词条属性列表',
              synced_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP COMMENT '同步时间',
              PRIMARY KEY (uid, avatar_id),
              INDEX idx_avatar_scores_total_score (total_score)
            ) COMMENT='角色圣遗物评分汇总'
            """,
            token).ConfigureAwait(false);

        await ExecuteNonQueryAsync(
            connection,
            """
            CREATE TABLE IF NOT EXISTS hutao_avatar_relic_scores (
              uid VARCHAR(32) NOT NULL COMMENT '游戏 UID',
              avatar_id BIGINT UNSIGNED NOT NULL COMMENT '角色 ID',
              equip_pos INT NOT NULL COMMENT '圣遗物部位',
              reliquary_id BIGINT UNSIGNED NULL COMMENT '圣遗物 ID',
              score DECIMAL(10,4) NOT NULL COMMENT '单件圣遗物评分，使用胡桃我的角色页同款算法',
              score_algorithm VARCHAR(64) NOT NULL COMMENT '评分算法版本',
              scored_sub_properties_json JSON NULL COMMENT '参与评分的副词条明细',
              synced_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP COMMENT '同步时间',
              PRIMARY KEY (uid, avatar_id, equip_pos),
              INDEX idx_avatar_relic_scores_reliquary_id (reliquary_id),
              INDEX idx_avatar_relic_scores_score (score)
            ) COMMENT='角色圣遗物评分明细'
            """,
            token).ConfigureAwait(false);
    }

    private static async ValueTask UpsertAvatarScoreAsync(MySqlConnection connection, string uid, AvatarScoreSnapshot snapshot, CancellationToken token)
    {
        await ExecuteNonQueryAsync(
            connection,
            """
            INSERT INTO hutao_avatar_scores
            (uid, avatar_id, total_score, score_algorithm, recommended_sub_properties_json)
            VALUES (@uid, @avatar_id, @total_score, @score_algorithm, @recommended_sub_properties_json)
            ON DUPLICATE KEY UPDATE total_score=VALUES(total_score),
              score_algorithm=VALUES(score_algorithm),
              recommended_sub_properties_json=VALUES(recommended_sub_properties_json),
              synced_at=CURRENT_TIMESTAMP
            """,
            token,
            ("@uid", uid),
            ("@avatar_id", snapshot.AvatarId),
            ("@total_score", snapshot.TotalScore),
            ("@score_algorithm", "hutao-reliquary-score-v1"),
            ("@recommended_sub_properties_json", snapshot.RecommendedSubPropertiesJson)).ConfigureAwait(false);

        foreach (ReliquaryScoreSnapshot reliquary in snapshot.Reliquaries)
        {
            await ExecuteNonQueryAsync(
                connection,
                """
                INSERT INTO hutao_avatar_relic_scores
                (uid, avatar_id, equip_pos, reliquary_id, score, score_algorithm, scored_sub_properties_json)
                VALUES (@uid, @avatar_id, @equip_pos, @reliquary_id, @score, @score_algorithm, @scored_sub_properties_json)
                ON DUPLICATE KEY UPDATE reliquary_id=VALUES(reliquary_id),
                  score=VALUES(score),
                  score_algorithm=VALUES(score_algorithm),
                  scored_sub_properties_json=VALUES(scored_sub_properties_json),
                  synced_at=CURRENT_TIMESTAMP
                """,
                token,
                ("@uid", uid),
                ("@avatar_id", snapshot.AvatarId),
                ("@equip_pos", reliquary.EquipPosition),
                ("@reliquary_id", reliquary.ReliquaryId),
                ("@score", reliquary.Score),
                ("@score_algorithm", "hutao-reliquary-score-v1"),
                ("@scored_sub_properties_json", reliquary.ScoredSubPropertiesJson)).ConfigureAwait(false);
        }
    }

    private static string SerializeScoredSubProperties(
        ImmutableArray<FightProperty> recommendedSubProperties,
        ImmutableArray<WebReliquaryProperty> subProperties,
        EnergyType energyType,
        bool isCritEffective)
    {
        bool hasCritHurt = isCritEffective || recommendedSubProperties.Contains(FightProperty.FIGHT_PROP_CRITICAL_HURT);

        object[] rows = [.. subProperties.Select(subProperty =>
        {
            double weight = GetReliquaryScoreWeight(subProperty.PropertyType, recommendedSubProperties, hasCritHurt, energyType, isCritEffective);

            return new
            {
                PropertyType = (int)subProperty.PropertyType,
                PropertyName = subProperty.PropertyType.ToString(),
                subProperty.Value,
                Rolls = subProperty.Times + 1,
                Weight = weight,
                IsScored = weight > 0,
            };
        })];

        return JsonSerializer.Serialize(rows, JsonOptions.Default);
    }

    private static double GetReliquaryScoreWeight(
        FightProperty propertyType,
        ImmutableArray<FightProperty> recommendedSubProperties,
        bool hasCritHurt,
        EnergyType energyType,
        bool isCritEffective)
    {
        if (isCritEffective && propertyType is FightProperty.FIGHT_PROP_CRITICAL or FightProperty.FIGHT_PROP_CRITICAL_HURT)
        {
            return 1.0;
        }

        bool isRecommended = recommendedSubProperties.Contains(propertyType);
        if (propertyType is FightProperty.FIGHT_PROP_CHARGE_EFFICIENCY && !isRecommended)
        {
            if (energyType is not EnergyType.SPECIAL_ENERGY_NONE)
            {
                return hasCritHurt ? 0 : 1.0;
            }

            return hasCritHurt ? 0.2 : 1.0;
        }

        if (!isRecommended)
        {
            return 0;
        }

        double weight = 1.0;
        if (propertyType is FightProperty.FIGHT_PROP_HP or FightProperty.FIGHT_PROP_ATTACK or FightProperty.FIGHT_PROP_DEFENSE)
        {
            weight *= 0.5;
        }

        return weight;
    }

    public async ValueTask SyncGachaArchiveAsync(GachaArchive archive, IEnumerable<GachaItem> items, IEnumerable<BeyondGachaItem> beyondItems, CancellationToken token = default)
    {
        TraceDebug($"SyncGachaArchiveAsync enter uid={archive.Uid}");

        string uid = archive.Uid;
        await ExecuteAsync("gacha", async connection =>
        {
            await UpsertAccountAsync(connection, uid, default, token).ConfigureAwait(false);
            await ExecuteNonQueryAsync(connection, "DELETE FROM hutao_gacha_items WHERE uid=@uid", token, ("@uid", uid)).ConfigureAwait(false);
            await ExecuteNonQueryAsync(connection, "DELETE FROM hutao_beyond_gacha_items WHERE uid=@uid", token, ("@uid", uid)).ConfigureAwait(false);

            foreach (GachaItem item in items)
            {
                await ExecuteNonQueryAsync(
                    connection,
                    """
                    INSERT INTO hutao_gacha_items
                    (uid, query_type, gacha_id, gacha_type, item_id, time, raw_json)
                    VALUES (@uid, @query_type, @gacha_id, @gacha_type, @item_id, @time, @raw_json)
                    """,
                    token,
                    ("@uid", uid),
                    ("@query_type", (int)item.QueryType),
                    ("@gacha_id", item.Id),
                    ("@gacha_type", (int)item.GachaType),
                    ("@item_id", item.ItemId),
                    ("@time", item.Time.DateTime),
                    ("@raw_json", JsonSerializer.Serialize(item, JsonOptions.Default))).ConfigureAwait(false);
            }

            foreach (BeyondGachaItem item in beyondItems)
            {
                await ExecuteNonQueryAsync(
                    connection,
                    """
                    INSERT INTO hutao_beyond_gacha_items
                    (uid, query_type, gacha_id, gacha_type, schedule_id, item_id, is_up, time, raw_json)
                    VALUES (@uid, @query_type, @gacha_id, @gacha_type, @schedule_id, @item_id, @is_up, @time, @raw_json)
                    """,
                    token,
                    ("@uid", uid),
                    ("@query_type", (int)item.QueryType),
                    ("@gacha_id", item.Id),
                    ("@gacha_type", (int)item.GachaType),
                    ("@schedule_id", item.ScheduleId),
                    ("@item_id", item.ItemId),
                    ("@is_up", item.IsUp),
                    ("@time", item.Time.DateTime),
                    ("@raw_json", JsonSerializer.Serialize(item, JsonOptions.Default))).ConfigureAwait(false);
            }
        }, token).ConfigureAwait(false);
    }

    public async ValueTask SyncDailyNoteAsync(DailyNoteEntry entry, CancellationToken token = default)
    {
        if (entry.DailyNote is not { } dailyNote)
        {
            TraceDebug($"SyncDailyNoteAsync skip uid={entry.Uid} dailyNote=null");
            return;
        }

        TraceDebug($"SyncDailyNoteAsync enter uid={entry.Uid}");

        await ExecuteAsync("daily-note", async connection =>
        {
            await UpsertAccountAsync(connection, entry.Uid, entry.UserGameRole?.Nickname, token).ConfigureAwait(false);
            await ExecuteNonQueryAsync(
                connection,
                """
                INSERT INTO hutao_daily_notes
                (uid, refresh_time, current_resin, max_resin, resin_recovery_time, current_home_coin, max_home_coin, home_coin_recovery_time,
                 finished_task_num, total_task_num, current_expedition_num, max_expedition_num, transformer_json, daily_task_json, archon_quest_json,
                 notify_config_json, raw_json)
                VALUES
                (@uid, @refresh_time, @current_resin, @max_resin, @resin_recovery_time, @current_home_coin, @max_home_coin, @home_coin_recovery_time,
                 @finished_task_num, @total_task_num, @current_expedition_num, @max_expedition_num, @transformer_json, @daily_task_json, @archon_quest_json,
                 @notify_config_json, @raw_json)
                ON DUPLICATE KEY UPDATE
                refresh_time=VALUES(refresh_time), current_resin=VALUES(current_resin), max_resin=VALUES(max_resin),
                resin_recovery_time=VALUES(resin_recovery_time), current_home_coin=VALUES(current_home_coin), max_home_coin=VALUES(max_home_coin),
                home_coin_recovery_time=VALUES(home_coin_recovery_time), finished_task_num=VALUES(finished_task_num), total_task_num=VALUES(total_task_num),
                current_expedition_num=VALUES(current_expedition_num), max_expedition_num=VALUES(max_expedition_num), transformer_json=VALUES(transformer_json),
                daily_task_json=VALUES(daily_task_json), archon_quest_json=VALUES(archon_quest_json), notify_config_json=VALUES(notify_config_json),
                raw_json=VALUES(raw_json), synced_at=CURRENT_TIMESTAMP
                """,
                token,
                ("@uid", entry.Uid),
                ("@refresh_time", entry.RefreshTime.UtcDateTime),
                ("@current_resin", dailyNote.CurrentResin),
                ("@max_resin", dailyNote.MaxResin),
                ("@resin_recovery_time", dailyNote.ResinRecoveryTime),
                ("@current_home_coin", dailyNote.CurrentHomeCoin),
                ("@max_home_coin", dailyNote.MaxHomeCoin),
                ("@home_coin_recovery_time", dailyNote.HomeCoinRecoveryTime),
                ("@finished_task_num", dailyNote.FinishedTaskNum),
                ("@total_task_num", dailyNote.TotalTaskNum),
                ("@current_expedition_num", dailyNote.CurrentExpeditionNum),
                ("@max_expedition_num", dailyNote.MaxExpeditionNum),
                ("@transformer_json", JsonSerializer.Serialize(dailyNote.Transformer, JsonOptions.Default)),
                ("@daily_task_json", JsonSerializer.Serialize(dailyNote.DailyTask, JsonOptions.Default)),
                ("@archon_quest_json", JsonSerializer.Serialize(dailyNote.ArchonQuestProgress, JsonOptions.Default)),
                ("@notify_config_json", JsonSerializer.Serialize(CreateNotifyConfig(entry), JsonOptions.Default)),
                ("@raw_json", JsonSerializer.Serialize(dailyNote, JsonOptions.Default))).ConfigureAwait(false);

            await ExecuteNonQueryAsync(connection, "DELETE FROM hutao_daily_note_expeditions WHERE uid=@uid", token, ("@uid", entry.Uid)).ConfigureAwait(false);

            for (int i = 0; i < dailyNote.Expeditions.Count; i++)
            {
                DailyNoteExpedition expedition = dailyNote.Expeditions[i];
                await UpsertMetaImageByUrlAsync(connection, "DailyNoteAvatarSideIcon", expedition.AvatarSideIcon.ToString(), token).ConfigureAwait(false);
                await ExecuteNonQueryAsync(
                    connection,
                    """
                    INSERT INTO hutao_daily_note_expeditions
                    (uid, slot_index, avatar_side_icon, status, remained_time, raw_json)
                    VALUES (@uid, @slot_index, @avatar_side_icon, @status, @remained_time, @raw_json)
                    """,
                    token,
                    ("@uid", entry.Uid),
                    ("@slot_index", i),
                    ("@avatar_side_icon", expedition.AvatarSideIcon.ToString()),
                    ("@status", expedition.Status.ToString()),
                    ("@remained_time", expedition.RemainedTime),
                    ("@raw_json", JsonSerializer.Serialize(expedition, JsonOptions.Default))).ConfigureAwait(false);
            }
        }, token).ConfigureAwait(false);
    }

    private static object CreateNotifyConfig(DailyNoteEntry entry)
    {
        return new
        {
            entry.ResinNotifyThreshold,
            entry.ResinNotifySuppressed,
            entry.ResinDotVisible,
            entry.HomeCoinNotifyThreshold,
            entry.HomeCoinNotifySuppressed,
            entry.HomeCoinDotVisible,
            entry.TransformerNotify,
            entry.TransformerNotifySuppressed,
            entry.TransformerDotVisible,
            entry.DailyTaskNotify,
            entry.DailyTaskNotifySuppressed,
            entry.DailyTaskDotVisible,
            entry.ExpeditionNotify,
            entry.ExpeditionNotifySuppressed,
            entry.ExpeditionDotVisible,
        };
    }

    public void StartMetadataSyncOnce()
    {
        if (metadataSynced)
        {
            return;
        }

        metadataSyncTask ??= SyncMetadataOnceAsync(CancellationToken.None);
    }

    private async Task SyncMetadataOnceAsync(CancellationToken token)
    {
        try
        {
            if (!await metadataService.InitializeAsync().ConfigureAwait(false))
            {
                return;
            }

            metadataSynced = await SyncMetadataAsync("zh-cn", token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            TraceDebug($"metadata: failed {ex.GetType().Name}: {ex.Message}");
            logger.LogWarning(ex, "Failed to sync metadata to MySQL");
        }
        finally
        {
            if (!metadataSynced)
            {
                metadataSyncTask = default;
            }
        }
    }

    private async ValueTask<bool> SyncMetadataAsync(string lang, CancellationToken token)
    {
        ImmutableArray<MetaAvatar> avatars = await metadataService.GetAvatarArrayAsync(token).ConfigureAwait(false);
        ImmutableArray<MetaWeapon> weapons = await metadataService.GetWeaponArrayAsync(token).ConfigureAwait(false);
        ImmutableArray<MetaReliquary> reliquaries = await metadataService.GetReliquaryArrayAsync(token).ConfigureAwait(false);
        ImmutableArray<MetaReliquarySet> reliquarySets = await metadataService.GetReliquarySetArrayAsync(token).ConfigureAwait(false);
        ImmutableArray<Material> materials = await metadataService.GetMaterialArrayAsync(token).ConfigureAwait(false);
        ImmutableArray<DisplayItem> displayItems = await metadataService.GetDisplayItemArrayAsync(token).ConfigureAwait(false);
        ImmutableArray<BeyondItem> beyondItems = await metadataService.FromCacheOrFileAsync<BeyondItem>(MetadataFileStrategies.BeyondItem, token).ConfigureAwait(false);
        ImmutableArray<GachaEvent> gachaEvents = await metadataService.GetGachaEventArrayAsync(token).ConfigureAwait(false);
        ImmutableArray<ReliquaryMainAffix> reliquaryMainAffixes = await metadataService.GetReliquaryMainAffixArrayAsync(token).ConfigureAwait(false);
        ImmutableArray<ReliquaryMainAffixLevel> reliquaryMainAffixLevels = await metadataService.GetReliquaryMainAffixLevelArrayAsync(token).ConfigureAwait(false);
        ImmutableArray<ReliquarySubAffix> reliquarySubAffixes = await metadataService.GetReliquarySubAffixArrayAsync(token).ConfigureAwait(false);
        ImmutableArray<GrowCurve> avatarCurves = await metadataService.GetAvatarCurveArrayAsync(token).ConfigureAwait(false);
        ImmutableArray<GrowCurve> weaponCurves = await metadataService.GetWeaponCurveArrayAsync(token).ConfigureAwait(false);
        ImmutableArray<MetaMonster> monsters = await metadataService.GetMonsterArrayAsync(token).ConfigureAwait(false);
        ImmutableArray<GrowCurve> monsterCurves = await metadataService.GetMonsterCurveArrayAsync(token).ConfigureAwait(false);
        ImmutableArray<Promote> avatarPromotes = await metadataService.GetAvatarPromoteArrayAsync(token).ConfigureAwait(false);
        ImmutableArray<Promote> weaponPromotes = await metadataService.GetWeaponPromoteArrayAsync(token).ConfigureAwait(false);

        TraceDebug(
            $"metadata: loaded avatars={avatars.Length}, weapons={weapons.Length}, monsters={monsters.Length}, reliquaries={reliquaries.Length}, reliquarySets={reliquarySets.Length}, materials={materials.Length}, displayItems={displayItems.Length}, beyondItems={beyondItems.Length}, gachaEvents={gachaEvents.Length}, reliquaryMainAffixes={reliquaryMainAffixes.Length}, reliquaryMainAffixLevels={reliquaryMainAffixLevels.Length}, reliquarySubAffixes={reliquarySubAffixes.Length}, avatarCurves={avatarCurves.Length}, weaponCurves={weaponCurves.Length}, monsterCurves={monsterCurves.Length}, avatarPromotes={avatarPromotes.Length}, weaponPromotes={weaponPromotes.Length}");

        int reliquaryIdCount = reliquaries.Sum(reliquary => reliquary.Ids.Length);
        string metadataVersion = ComputeMetadataHash(
        new string[]
        {
            avatars.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            weapons.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            reliquaryIdCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            reliquarySets.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            materials.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            displayItems.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            beyondItems.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            gachaEvents.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            reliquaryMainAffixes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            reliquaryMainAffixLevels.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            reliquarySubAffixes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            avatarCurves.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            weaponCurves.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            monsters.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            monsterCurves.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            avatarPromotes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            weaponPromotes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });
        ImmutableArray<MetadataTableSyncState> tableStates = CreateMetadataTableSyncStates(
            lang,
            avatars,
            weapons,
            reliquaries,
            reliquarySets,
            materials,
            displayItems,
            beyondItems,
            gachaEvents,
            reliquaryMainAffixes,
            reliquaryMainAffixLevels,
            reliquarySubAffixes,
            avatarCurves,
            weaponCurves,
            monsters,
            monsterCurves,
            avatarPromotes,
            weaponPromotes);

        bool syncSucceeded = await ExecuteAsync("metadata", async connection =>
        {
            await EnsureMetadataTablesAsync(connection, token).ConfigureAwait(false);
            await BackfillAvatarScoresFromExistingRowsAsync(connection, token).ConfigureAwait(false);

            if (await IsMetadataCompleteAsync(connection, tableStates, token).ConfigureAwait(false))
            {
                TraceDebug("metadata: skipped because every table hash is already complete");
                NotifyMetadataSyncSkipped(tableStates);
                return;
            }

            await SyncRegionMetadataAsync(connection, lang, token).ConfigureAwait(false);
            await SyncEnumMetadataAsync<ElementName>(connection, "hutao_meta_elements", "element", lang, token).ConfigureAwait(false);
            await SyncEnumMetadataAsync<WeaponType>(connection, "hutao_meta_weapon_types", "weapon_type", lang, token).ConfigureAwait(false);
            await SyncEnumMetadataAsync<EquipType>(connection, "hutao_meta_equip_types", "equip_type", lang, token).ConfigureAwait(false);
            await SyncEnumMetadataAsync<FightProperty>(connection, "hutao_meta_fight_properties", "property_type", lang, token).ConfigureAwait(false);
            await SyncEnumMetadataAsync<WebGachaType>(connection, "hutao_meta_gacha_types", "gacha_type", lang, token).ConfigureAwait(false);

            await SyncSupplementalMetadataAsync(
                connection,
                lang,
                displayItems,
                beyondItems,
                gachaEvents,
                reliquaryMainAffixes,
                reliquaryMainAffixLevels,
                reliquarySubAffixes,
                avatarCurves,
                weaponCurves,
                avatars,
                weapons,
                monsters,
                monsterCurves,
                avatarPromotes,
                weaponPromotes,
                token).ConfigureAwait(false);

            if (await IsCoreMetadataCompleteAsync(connection, lang, avatars.Length, weapons.Length, reliquaryIdCount, reliquarySets.Length, materials.Length, token).ConfigureAwait(false))
            {
                await UpsertMetaSyncStatesAsync(connection, tableStates, token).ConfigureAwait(false);
                await UpsertMetaVersionAsync(connection, lang, metadataVersion, token).ConfigureAwait(false);
                TraceDebug("metadata: core tables already complete, supplemental tables synced");
                NotifyMetadataSyncCompleted(tableStates);
                return;
            }

            foreach (MetaAvatar avatar in avatars)
            {
                await ExecuteNonQueryAsync(
                    connection,
                    """
                    INSERT INTO hutao_meta_avatars
                    (avatar_id, lang, name, element, weapon_type, rarity, icon, side_icon, card_image, raw_json)
                    VALUES (@id, @lang, @name, @element, @weapon_type, @rarity, @icon, @side_icon, @card_image, @raw_json)
                    ON DUPLICATE KEY UPDATE name=VALUES(name), element=VALUES(element), weapon_type=VALUES(weapon_type), rarity=VALUES(rarity),
                    icon=VALUES(icon), side_icon=VALUES(side_icon), card_image=VALUES(card_image), raw_json=VALUES(raw_json)
                    """,
                    token,
                    ("@id", (uint)avatar.Id),
                    ("@lang", lang),
                    ("@name", avatar.Name),
                    ("@element", avatar.FetterInfo.Vision),
                    ("@weapon_type", (int)avatar.Weapon),
                    ("@rarity", (int)avatar.Quality),
                    ("@icon", avatar.Icon),
                    ("@side_icon", avatar.SideIcon),
                    ("@card_image", avatar.NameCard.PicturePrefix),
                    ("@raw_json", TrySerializeMetadataJson(avatar))).ConfigureAwait(false);

                await UpsertMetaImageAsync(connection, "AvatarIcon", $"{avatar.Icon}.png", token).ConfigureAwait(false);
                await UpsertMetaImageAsync(connection, "AvatarIcon", $"{avatar.SideIcon}.png", token).ConfigureAwait(false);
                await UpsertMetaImageAsync(connection, "NameCardPic", $"{avatar.NameCard.PicturePrefix}_P.png", token).ConfigureAwait(false);
                await UpsertMetaItemAsync(connection, (uint)avatar.Id, lang, avatar.Name, "Avatar", (int)avatar.Quality, avatar.Icon, avatar.Description, avatar, token).ConfigureAwait(false);

                foreach (MetaSkill skill in avatar.SkillDepot.CompositeSkills)
                {
                    await SyncAvatarSkillMetadataAsync(connection, avatar, skill, null, lang, token).ConfigureAwait(false);
                }

                for (int i = 0; i < avatar.SkillDepot.Talents.Length; i++)
                {
                    MetaSkill talent = avatar.SkillDepot.Talents[i];
                    await SyncAvatarConstellationMetadataAsync(connection, avatar, talent, i + 1, lang, token).ConfigureAwait(false);
                }
            }

            foreach (MetaWeapon weapon in weapons)
            {
                await ExecuteNonQueryAsync(
                    connection,
                    """
                    INSERT INTO hutao_meta_weapons
                    (weapon_id, lang, name, weapon_type, rarity, icon, description, raw_json)
                    VALUES (@id, @lang, @name, @weapon_type, @rarity, @icon, @description, @raw_json)
                    ON DUPLICATE KEY UPDATE name=VALUES(name), weapon_type=VALUES(weapon_type), rarity=VALUES(rarity),
                    icon=VALUES(icon), description=VALUES(description), raw_json=VALUES(raw_json)
                    """,
                    token,
                    ("@id", (uint)weapon.Id),
                    ("@lang", lang),
                    ("@name", weapon.Name),
                    ("@weapon_type", (int)weapon.WeaponType),
                    ("@rarity", (int)weapon.RankLevel),
                    ("@icon", weapon.Icon),
                    ("@description", weapon.Description),
                    ("@raw_json", TrySerializeMetadataJson(weapon))).ConfigureAwait(false);

                await UpsertMetaImageAsync(connection, "EquipIcon", $"{weapon.Icon}.png", token).ConfigureAwait(false);
                await UpsertMetaItemAsync(connection, (uint)weapon.Id, lang, weapon.Name, "Weapon", (int)weapon.RankLevel, weapon.Icon, weapon.Description, weapon, token).ConfigureAwait(false);
            }

            foreach (MetaReliquarySet set in reliquarySets)
            {
                await ExecuteNonQueryAsync(
                    connection,
                    """
                    INSERT INTO hutao_meta_reliquary_sets
                    (set_id, lang, name, affixes_json, raw_json)
                    VALUES (@id, @lang, @name, @affixes_json, @raw_json)
                    ON DUPLICATE KEY UPDATE name=VALUES(name), affixes_json=VALUES(affixes_json), raw_json=VALUES(raw_json)
                    """,
                    token,
                    ("@id", (uint)set.SetId),
                    ("@lang", lang),
                    ("@name", set.Name),
                    ("@affixes_json", JsonSerializer.Serialize(set.Descriptions, JsonOptions.Default)),
                    ("@raw_json", TrySerializeMetadataJson(set))).ConfigureAwait(false);
            }

            foreach (MetaReliquary reliquary in reliquaries)
            {
                foreach (uint id in reliquary.Ids.Select(id => (uint)id))
                {
                    await ExecuteNonQueryAsync(
                        connection,
                        """
                        INSERT INTO hutao_meta_reliquaries
                        (reliquary_id, lang, name, set_id, equip_type, rarity, icon, description, raw_json)
                        VALUES (@id, @lang, @name, @set_id, @equip_type, @rarity, @icon, @description, @raw_json)
                        ON DUPLICATE KEY UPDATE name=VALUES(name), set_id=VALUES(set_id), equip_type=VALUES(equip_type), rarity=VALUES(rarity),
                        icon=VALUES(icon), description=VALUES(description), raw_json=VALUES(raw_json)
                        """,
                        token,
                        ("@id", id),
                        ("@lang", lang),
                        ("@name", reliquary.Name),
                        ("@set_id", (uint)reliquary.SetId),
                        ("@equip_type", (int)reliquary.EquipType),
                        ("@rarity", (int)reliquary.RankLevel),
                        ("@icon", reliquary.Icon),
                        ("@description", reliquary.Description),
                        ("@raw_json", TrySerializeMetadataJson(reliquary))).ConfigureAwait(false);

                    await UpsertMetaImageAsync(connection, "RelicIcon", $"{reliquary.Icon}.png", token).ConfigureAwait(false);
                    await UpsertMetaItemAsync(connection, id, lang, reliquary.Name, "Reliquary", (int)reliquary.RankLevel, reliquary.Icon, reliquary.Description, reliquary, token).ConfigureAwait(false);
                }
            }

            foreach (Material material in materials)
            {
                await ExecuteNonQueryAsync(
                    connection,
                    """
                    INSERT INTO hutao_meta_materials
                    (material_id, lang, name, material_type, rank_level, icon, description, raw_json)
                    VALUES (@id, @lang, @name, @material_type, @rank_level, @icon, @description, @raw_json)
                    ON DUPLICATE KEY UPDATE name=VALUES(name), material_type=VALUES(material_type), rank_level=VALUES(rank_level),
                    icon=VALUES(icon), description=VALUES(description), raw_json=VALUES(raw_json)
                    """,
                    token,
                    ("@id", (uint)material.Id),
                    ("@lang", lang),
                    ("@name", material.Name),
                    ("@material_type", material.MaterialType.ToString()),
                    ("@rank_level", (int)material.RankLevel),
                    ("@icon", material.Icon),
                    ("@description", material.Description),
                    ("@raw_json", TrySerializeMetadataJson(material))).ConfigureAwait(false);

                await UpsertMetaImageAsync(connection, "ItemIcon", $"{material.Icon}.png", token).ConfigureAwait(false);
                await UpsertMetaItemAsync(connection, (uint)material.Id, lang, material.Name, "Material", (int)material.RankLevel, material.Icon, material.Description, material, token).ConfigureAwait(false);
            }

            foreach (DisplayItem item in displayItems)
            {
                await UpsertMetaDisplayItemAsync(connection, item, lang, token).ConfigureAwait(false);
                await UpsertMetaImageAsync(connection, "ItemIcon", $"{item.Icon}.png", token).ConfigureAwait(false);
                await UpsertMetaItemAsync(connection, (uint)item.Id, lang, item.Name, "DisplayItem", (int)item.RankLevel, item.Icon, item.Description, item, token).ConfigureAwait(false);
            }

            foreach (BeyondItem item in beyondItems)
            {
                await ExecuteNonQueryAsync(
                    connection,
                    """
                    INSERT INTO hutao_meta_beyond_items
                    (item_id, lang, name, type, type_description, rank_level, icon, description, raw_json)
                    VALUES (@id, @lang, @name, @type, @type_description, @rank_level, @icon, @description, @raw_json)
                    ON DUPLICATE KEY UPDATE name=VALUES(name), type=VALUES(type), type_description=VALUES(type_description),
                    rank_level=VALUES(rank_level), icon=VALUES(icon), description=VALUES(description), raw_json=VALUES(raw_json)
                    """,
                    token,
                    ("@id", (uint)item.Id),
                    ("@lang", lang),
                    ("@name", item.Name),
                    ("@type", item.Type),
                    ("@type_description", item.TypeDescription),
                    ("@rank_level", (int)item.RankLevel),
                    ("@icon", item.Icon),
                    ("@description", item.Description),
                    ("@raw_json", TrySerializeMetadataJson(item))).ConfigureAwait(false);

                await UpsertMetaImageAsync(connection, "BeyondItemIcon", $"{item.Icon}.png", token).ConfigureAwait(false);
                await UpsertMetaItemAsync(connection, (uint)item.Id, lang, item.Name, "BeyondItem", (int)item.RankLevel, item.Icon, item.Description, item, token).ConfigureAwait(false);
            }

            foreach (GachaEvent gachaEvent in gachaEvents)
            {
                await ExecuteNonQueryAsync(
                    connection,
                    """
                    INSERT INTO hutao_meta_gacha_events
                    (name, lang, version, sort_order, banner_url, banner2_url, from_time, to_time, gacha_type, up_orange_json, up_purple_json, raw_json)
                    VALUES (@name, @lang, @version, @sort_order, @banner_url, @banner2_url, @from_time, @to_time, @gacha_type, @up_orange_json, @up_purple_json, @raw_json)
                    ON DUPLICATE KEY UPDATE version=VALUES(version), sort_order=VALUES(sort_order), banner_url=VALUES(banner_url),
                    banner2_url=VALUES(banner2_url), from_time=VALUES(from_time), to_time=VALUES(to_time), gacha_type=VALUES(gacha_type),
                    up_orange_json=VALUES(up_orange_json), up_purple_json=VALUES(up_purple_json), raw_json=VALUES(raw_json)
                    """,
                    token,
                    ("@name", gachaEvent.Name),
                    ("@lang", lang),
                    ("@version", gachaEvent.Version),
                    ("@sort_order", gachaEvent.Order),
                    ("@banner_url", gachaEvent.Banner.ToString()),
                    ("@banner2_url", gachaEvent.Banner2?.ToString()),
                    ("@from_time", gachaEvent.From.UtcDateTime),
                    ("@to_time", gachaEvent.To.UtcDateTime),
                    ("@gacha_type", (int)gachaEvent.Type),
                    ("@up_orange_json", JsonSerializer.Serialize(gachaEvent.UpOrangeList, JsonOptions.Default)),
                    ("@up_purple_json", JsonSerializer.Serialize(gachaEvent.UpPurpleList, JsonOptions.Default)),
                    ("@raw_json", TrySerializeMetadataJson(gachaEvent))).ConfigureAwait(false);
            }

            foreach (ReliquaryMainAffix affix in reliquaryMainAffixes)
            {
                await ExecuteNonQueryAsync(
                    connection,
                    """
                    INSERT INTO hutao_meta_reliquary_main_affixes
                    (affix_id, lang, property_type, raw_json)
                    VALUES (@affix_id, @lang, @property_type, @raw_json)
                    ON DUPLICATE KEY UPDATE property_type=VALUES(property_type), raw_json=VALUES(raw_json)
                    """,
                    token,
                    ("@affix_id", (uint)affix.Id),
                    ("@lang", lang),
                    ("@property_type", (int)affix.Type),
                    ("@raw_json", TrySerializeMetadataJson(affix))).ConfigureAwait(false);
            }

            foreach (ReliquaryMainAffixLevel level in reliquaryMainAffixLevels)
            {
                await ExecuteNonQueryAsync(
                    connection,
                    """
                    INSERT INTO hutao_meta_reliquary_main_affix_levels
                    (rank_level, level, lang, properties_json, raw_json)
                    VALUES (@rank_level, @level, @lang, @properties_json, @raw_json)
                    ON DUPLICATE KEY UPDATE properties_json=VALUES(properties_json), raw_json=VALUES(raw_json)
                    """,
                    token,
                    ("@rank_level", (int)level.Rank),
                    ("@level", level.Level),
                    ("@lang", lang),
                    ("@properties_json", SerializeTypeValueCollection(level.Properties)),
                    ("@raw_json", TrySerializeMetadataJson(level))).ConfigureAwait(false);
            }

            foreach (ReliquarySubAffix affix in reliquarySubAffixes)
            {
                await ExecuteNonQueryAsync(
                    connection,
                    """
                    INSERT INTO hutao_meta_reliquary_sub_affixes
                    (affix_id, lang, property_type, affix_value, raw_json)
                    VALUES (@affix_id, @lang, @property_type, @affix_value, @raw_json)
                    ON DUPLICATE KEY UPDATE property_type=VALUES(property_type), affix_value=VALUES(affix_value), raw_json=VALUES(raw_json)
                    """,
                    token,
                    ("@affix_id", (uint)affix.Id),
                    ("@lang", lang),
                    ("@property_type", (int)affix.Type),
                    ("@affix_value", affix.Value),
                    ("@raw_json", TrySerializeMetadataJson(affix))).ConfigureAwait(false);
            }

            await SyncGrowCurvesAsync(connection, "hutao_meta_avatar_curves", avatarCurves, lang, token).ConfigureAwait(false);
            await SyncGrowCurvesAsync(connection, "hutao_meta_weapon_curves", weaponCurves, lang, token).ConfigureAwait(false);
            await SyncPromotesAsync(connection, "hutao_meta_avatar_promotes", avatarPromotes, lang, token).ConfigureAwait(false);
            await SyncPromotesAsync(connection, "hutao_meta_weapon_promotes", weaponPromotes, lang, token).ConfigureAwait(false);
            await UpsertMetaSyncStatesAsync(connection, tableStates, token).ConfigureAwait(false);
            await UpsertMetaVersionAsync(connection, lang, metadataVersion, token).ConfigureAwait(false);
            NotifyMetadataSyncCompleted(tableStates);
        }, token).ConfigureAwait(false);

        if (!syncSucceeded)
        {
            NotifyMetadataSyncFailed();
        }

        return syncSucceeded;
    }

    private static async ValueTask SyncSupplementalMetadataAsync(
        MySqlConnection connection,
        string lang,
        ImmutableArray<DisplayItem> displayItems,
        ImmutableArray<BeyondItem> beyondItems,
        ImmutableArray<GachaEvent> gachaEvents,
        ImmutableArray<ReliquaryMainAffix> reliquaryMainAffixes,
        ImmutableArray<ReliquaryMainAffixLevel> reliquaryMainAffixLevels,
        ImmutableArray<ReliquarySubAffix> reliquarySubAffixes,
        ImmutableArray<GrowCurve> avatarCurves,
        ImmutableArray<GrowCurve> weaponCurves,
        ImmutableArray<MetaAvatar> avatars,
        ImmutableArray<MetaWeapon> weapons,
        ImmutableArray<MetaMonster> monsters,
        ImmutableArray<GrowCurve> monsterCurves,
        ImmutableArray<Promote> avatarPromotes,
        ImmutableArray<Promote> weaponPromotes,
        CancellationToken token)
    {
        TraceDebug("metadata: syncing supplemental tables");

        foreach (ReliquaryMainAffixLevel level in reliquaryMainAffixLevels)
        {
            await ExecuteNonQueryAsync(
                connection,
                """
                INSERT INTO hutao_meta_reliquary_main_affix_levels
                (rank_level, level, lang, properties_json, raw_json)
                VALUES (@rank_level, @level, @lang, @properties_json, @raw_json)
                ON DUPLICATE KEY UPDATE properties_json=VALUES(properties_json), raw_json=VALUES(raw_json)
                """,
                token,
                ("@rank_level", (int)level.Rank),
                ("@level", level.Level),
                ("@lang", lang),
                ("@properties_json", SerializeTypeValueCollection(level.Properties)),
                ("@raw_json", TrySerializeMetadataJson(level))).ConfigureAwait(false);
        }

        TraceDebug("metadata: reliquary main affix level values synced");

        foreach (DisplayItem item in displayItems)
        {
            await UpsertMetaDisplayItemAsync(connection, item, lang, token).ConfigureAwait(false);
            await UpsertMetaImageAsync(connection, "ItemIcon", $"{item.Icon}.png", token).ConfigureAwait(false);
            await UpsertMetaItemAsync(connection, (uint)item.Id, lang, item.Name, "DisplayItem", (int)item.RankLevel, item.Icon, item.Description, item, token).ConfigureAwait(false);
        }

        foreach (BeyondItem item in beyondItems)
        {
            await ExecuteNonQueryAsync(
                connection,
                """
                INSERT INTO hutao_meta_beyond_items
                (item_id, lang, name, type, type_description, rank_level, icon, description, raw_json)
                VALUES (@id, @lang, @name, @type, @type_description, @rank_level, @icon, @description, @raw_json)
                ON DUPLICATE KEY UPDATE name=VALUES(name), type=VALUES(type), type_description=VALUES(type_description),
                rank_level=VALUES(rank_level), icon=VALUES(icon), description=VALUES(description), raw_json=VALUES(raw_json)
                """,
                token,
                ("@id", (uint)item.Id),
                ("@lang", lang),
                ("@name", item.Name),
                ("@type", item.Type),
                ("@type_description", item.TypeDescription),
                ("@rank_level", (int)item.RankLevel),
                ("@icon", item.Icon),
                ("@description", item.Description),
                ("@raw_json", TrySerializeMetadataJson(item))).ConfigureAwait(false);

            await UpsertMetaImageAsync(connection, "BeyondItemIcon", $"{item.Icon}.png", token).ConfigureAwait(false);
            await UpsertMetaItemAsync(connection, (uint)item.Id, lang, item.Name, "BeyondItem", (int)item.RankLevel, item.Icon, item.Description, item, token).ConfigureAwait(false);
        }

        foreach (GachaEvent gachaEvent in gachaEvents)
        {
            await ExecuteNonQueryAsync(
                connection,
                """
                INSERT INTO hutao_meta_gacha_events
                (name, lang, version, sort_order, banner_url, banner2_url, from_time, to_time, gacha_type, up_orange_json, up_purple_json, raw_json)
                VALUES (@name, @lang, @version, @sort_order, @banner_url, @banner2_url, @from_time, @to_time, @gacha_type, @up_orange_json, @up_purple_json, @raw_json)
                ON DUPLICATE KEY UPDATE version=VALUES(version), sort_order=VALUES(sort_order), banner_url=VALUES(banner_url),
                banner2_url=VALUES(banner2_url), from_time=VALUES(from_time), to_time=VALUES(to_time), gacha_type=VALUES(gacha_type),
                up_orange_json=VALUES(up_orange_json), up_purple_json=VALUES(up_purple_json), raw_json=VALUES(raw_json)
                """,
                token,
                ("@name", gachaEvent.Name),
                ("@lang", lang),
                ("@version", gachaEvent.Version),
                ("@sort_order", gachaEvent.Order),
                ("@banner_url", gachaEvent.Banner.ToString()),
                ("@banner2_url", gachaEvent.Banner2?.ToString()),
                ("@from_time", gachaEvent.From.UtcDateTime),
                ("@to_time", gachaEvent.To.UtcDateTime),
                ("@gacha_type", (int)gachaEvent.Type),
                ("@up_orange_json", JsonSerializer.Serialize(gachaEvent.UpOrangeList, JsonOptions.Default)),
                ("@up_purple_json", JsonSerializer.Serialize(gachaEvent.UpPurpleList, JsonOptions.Default)),
                ("@raw_json", TrySerializeMetadataJson(gachaEvent))).ConfigureAwait(false);
        }

        foreach (ReliquaryMainAffix affix in reliquaryMainAffixes)
        {
            await ExecuteNonQueryAsync(
                connection,
                """
                INSERT INTO hutao_meta_reliquary_main_affixes
                (affix_id, lang, property_type, raw_json)
                VALUES (@affix_id, @lang, @property_type, @raw_json)
                ON DUPLICATE KEY UPDATE property_type=VALUES(property_type), raw_json=VALUES(raw_json)
                """,
                token,
                ("@affix_id", (uint)affix.Id),
                ("@lang", lang),
                ("@property_type", (int)affix.Type),
                ("@raw_json", TrySerializeMetadataJson(affix))).ConfigureAwait(false);
        }

        foreach (ReliquaryMainAffixLevel level in reliquaryMainAffixLevels)
        {
            await ExecuteNonQueryAsync(
                connection,
                """
                INSERT INTO hutao_meta_reliquary_main_affix_levels
                (rank_level, level, lang, properties_json, raw_json)
                VALUES (@rank_level, @level, @lang, @properties_json, @raw_json)
                ON DUPLICATE KEY UPDATE properties_json=VALUES(properties_json), raw_json=VALUES(raw_json)
                """,
                token,
                ("@rank_level", (int)level.Rank),
                ("@level", level.Level),
                ("@lang", lang),
                ("@properties_json", SerializeTypeValueCollection(level.Properties)),
                ("@raw_json", TrySerializeMetadataJson(level))).ConfigureAwait(false);
        }

        foreach (ReliquarySubAffix affix in reliquarySubAffixes)
        {
            await ExecuteNonQueryAsync(
                connection,
                """
                INSERT INTO hutao_meta_reliquary_sub_affixes
                (affix_id, lang, property_type, affix_value, raw_json)
                VALUES (@affix_id, @lang, @property_type, @affix_value, @raw_json)
                ON DUPLICATE KEY UPDATE property_type=VALUES(property_type), affix_value=VALUES(affix_value), raw_json=VALUES(raw_json)
                """,
                token,
                ("@affix_id", (uint)affix.Id),
                ("@lang", lang),
                ("@property_type", (int)affix.Type),
                ("@affix_value", affix.Value),
                ("@raw_json", TrySerializeMetadataJson(affix))).ConfigureAwait(false);
        }

        await SyncGrowCurvesAsync(connection, "hutao_meta_avatar_curves", avatarCurves, lang, token).ConfigureAwait(false);
        await SyncGrowCurvesAsync(connection, "hutao_meta_weapon_curves", weaponCurves, lang, token).ConfigureAwait(false);
        await SyncWikiAvatarMetadataAsync(connection, avatars, lang, token).ConfigureAwait(false);
        await SyncWikiWeaponMetadataAsync(connection, weapons, lang, token).ConfigureAwait(false);
        await SyncWikiMonsterMetadataAsync(connection, monsters, lang, token).ConfigureAwait(false);
        await SyncGrowCurvesAsync(connection, "hutao_wiki_monster_curves", monsterCurves, lang, token).ConfigureAwait(false);
        await SyncPromotesAsync(connection, "hutao_meta_avatar_promotes", avatarPromotes, lang, token).ConfigureAwait(false);
        await SyncPromotesAsync(connection, "hutao_meta_weapon_promotes", weaponPromotes, lang, token).ConfigureAwait(false);

        TraceDebug("metadata: supplemental tables synced");
    }

    private static async ValueTask SyncWikiAvatarMetadataAsync(MySqlConnection connection, ImmutableArray<MetaAvatar> avatars, string lang, CancellationToken token)
    {
        foreach (MetaAvatar avatar in avatars)
        {
            await ExecuteNonQueryAsync(
                connection,
                """
                INSERT INTO hutao_wiki_avatars
                (avatar_id, lang, promote_id, sort_order, body_type, name, description, begin_time, quality, weapon_type, element, icon, side_icon, base_value_json, grow_curves_json, skill_depot_json, fetter_info_json, costumes_json, cultivation_items_json, name_card_json, raw_json)
                VALUES (@avatar_id, @lang, @promote_id, @sort_order, @body_type, @name, @description, @begin_time, @quality, @weapon_type, @element, @icon, @side_icon, @base_value_json, @grow_curves_json, @skill_depot_json, @fetter_info_json, @costumes_json, @cultivation_items_json, @name_card_json, @raw_json)
                ON DUPLICATE KEY UPDATE promote_id=VALUES(promote_id), sort_order=VALUES(sort_order), body_type=VALUES(body_type), name=VALUES(name),
                description=VALUES(description), begin_time=VALUES(begin_time), quality=VALUES(quality), weapon_type=VALUES(weapon_type), element=VALUES(element),
                icon=VALUES(icon), side_icon=VALUES(side_icon), base_value_json=VALUES(base_value_json), grow_curves_json=VALUES(grow_curves_json),
                skill_depot_json=VALUES(skill_depot_json), fetter_info_json=VALUES(fetter_info_json), costumes_json=VALUES(costumes_json),
                cultivation_items_json=VALUES(cultivation_items_json), name_card_json=VALUES(name_card_json), raw_json=VALUES(raw_json)
                """,
                token,
                ("@avatar_id", (uint)avatar.Id),
                ("@lang", lang),
                ("@promote_id", (uint)avatar.PromoteId),
                ("@sort_order", avatar.Sort),
                ("@body_type", (int)avatar.Body),
                ("@name", avatar.Name),
                ("@description", avatar.Description),
                ("@begin_time", avatar.BeginTime.UtcDateTime),
                ("@quality", (int)avatar.Quality),
                ("@weapon_type", (int)avatar.Weapon),
                ("@element", avatar.FetterInfo.Vision),
                ("@icon", avatar.Icon),
                ("@side_icon", avatar.SideIcon),
                ("@base_value_json", SerializeMetadataJsonOrNullLiteral(avatar.BaseValue)),
                ("@grow_curves_json", SerializeTypeValueCollection(avatar.GrowCurves)),
                ("@skill_depot_json", SerializeMetadataJsonOrNullLiteral(avatar.SkillDepot)),
                ("@fetter_info_json", SerializeMetadataJsonOrNullLiteral(avatar.FetterInfo)),
                ("@costumes_json", SerializeImmutableArray(avatar.Costumes)),
                ("@cultivation_items_json", SerializeImmutableArray(avatar.CultivationItems)),
                ("@name_card_json", SerializeMetadataJsonOrNullLiteral(avatar.NameCard)),
                ("@raw_json", TrySerializeMetadataJson(avatar))).ConfigureAwait(false);
        }
    }

    private static async ValueTask SyncWikiWeaponMetadataAsync(MySqlConnection connection, ImmutableArray<MetaWeapon> weapons, string lang, CancellationToken token)
    {
        foreach (MetaWeapon weapon in weapons)
        {
            await ExecuteNonQueryAsync(
                connection,
                """
                INSERT INTO hutao_wiki_weapons
                (weapon_id, lang, promote_id, sort_order, weapon_type, quality, name, description, icon, awaken_icon, grow_curves_json, affix_json, cultivation_items_json, raw_json)
                VALUES (@weapon_id, @lang, @promote_id, @sort_order, @weapon_type, @quality, @name, @description, @icon, @awaken_icon, @grow_curves_json, @affix_json, @cultivation_items_json, @raw_json)
                ON DUPLICATE KEY UPDATE promote_id=VALUES(promote_id), sort_order=VALUES(sort_order), weapon_type=VALUES(weapon_type),
                quality=VALUES(quality), name=VALUES(name), description=VALUES(description), icon=VALUES(icon), awaken_icon=VALUES(awaken_icon),
                grow_curves_json=VALUES(grow_curves_json), affix_json=VALUES(affix_json), cultivation_items_json=VALUES(cultivation_items_json), raw_json=VALUES(raw_json)
                """,
                token,
                ("@weapon_id", (uint)weapon.Id),
                ("@lang", lang),
                ("@promote_id", (uint)weapon.PromoteId),
                ("@sort_order", weapon.Sort),
                ("@weapon_type", (int)weapon.WeaponType),
                ("@quality", (int)weapon.RankLevel),
                ("@name", weapon.Name),
                ("@description", weapon.Description),
                ("@icon", weapon.Icon),
                ("@awaken_icon", weapon.AwakenIcon),
                ("@grow_curves_json", SerializeMetadataJsonOrNullLiteral(weapon.GrowCurves)),
                ("@affix_json", TrySerializeMetadataJson(weapon.Affix)),
                ("@cultivation_items_json", SerializeImmutableArray(weapon.CultivationItems)),
                ("@raw_json", TrySerializeMetadataJson(weapon))).ConfigureAwait(false);
        }
    }

    private static async ValueTask SyncWikiMonsterMetadataAsync(MySqlConnection connection, ImmutableArray<MetaMonster> monsters, string lang, CancellationToken token)
    {
        foreach (MetaMonster monster in monsters)
        {
            await ExecuteNonQueryAsync(
                connection,
                """
                INSERT INTO hutao_wiki_monsters
                (monster_id, describe_id, lang, monster_name, name, title, description, icon, monster_type, arkhe, affixes_json, drops_json, base_value_json, grow_curves_json, raw_json)
                VALUES (@monster_id, @describe_id, @lang, @monster_name, @name, @title, @description, @icon, @monster_type, @arkhe, @affixes_json, @drops_json, @base_value_json, @grow_curves_json, @raw_json)
                ON DUPLICATE KEY UPDATE describe_id=VALUES(describe_id), monster_name=VALUES(monster_name), name=VALUES(name), title=VALUES(title),
                description=VALUES(description), icon=VALUES(icon), monster_type=VALUES(monster_type), arkhe=VALUES(arkhe), affixes_json=VALUES(affixes_json),
                drops_json=VALUES(drops_json), base_value_json=VALUES(base_value_json), grow_curves_json=VALUES(grow_curves_json), raw_json=VALUES(raw_json)
                """,
                token,
                ("@monster_id", (uint)monster.Id),
                ("@describe_id", (uint)monster.DescribeId),
                ("@lang", lang),
                ("@monster_name", monster.MonsterName),
                ("@name", monster.Name),
                ("@title", monster.Title),
                ("@description", monster.Description),
                ("@icon", monster.Icon),
                ("@monster_type", (int)monster.Type),
                ("@arkhe", (int)monster.Arkhe),
                ("@affixes_json", JsonSerializer.Serialize(monster.Affixes, JsonOptions.Default)),
                ("@drops_json", SerializeImmutableArray(monster.Drops)),
                ("@base_value_json", TrySerializeMetadataJson(monster.BaseValue)),
                ("@grow_curves_json", TrySerializeMetadataJson(monster.GrowCurves)),
                ("@raw_json", TrySerializeMetadataJson(monster))).ConfigureAwait(false);

            await UpsertMetaImageAsync(connection, "MonsterIcon", $"{monster.Icon}.png", token).ConfigureAwait(false);
        }
    }

    private static ImmutableArray<MetadataTableSyncState> CreateMetadataTableSyncStates(
        string lang,
        ImmutableArray<MetaAvatar> avatars,
        ImmutableArray<MetaWeapon> weapons,
        ImmutableArray<MetaReliquary> reliquaries,
        ImmutableArray<MetaReliquarySet> reliquarySets,
        ImmutableArray<Material> materials,
        ImmutableArray<DisplayItem> displayItems,
        ImmutableArray<BeyondItem> beyondItems,
        ImmutableArray<GachaEvent> gachaEvents,
        ImmutableArray<ReliquaryMainAffix> reliquaryMainAffixes,
        ImmutableArray<ReliquaryMainAffixLevel> reliquaryMainAffixLevels,
        ImmutableArray<ReliquarySubAffix> reliquarySubAffixes,
        ImmutableArray<GrowCurve> avatarCurves,
        ImmutableArray<GrowCurve> weaponCurves,
        ImmutableArray<MetaMonster> monsters,
        ImmutableArray<GrowCurve> monsterCurves,
        ImmutableArray<Promote> avatarPromotes,
        ImmutableArray<Promote> weaponPromotes)
    {
        string[] itemHashes =
        [
            .. avatars.Select(avatar => $"Avatar|{(uint)avatar.Id}|{avatar.Name}|{(int)avatar.Quality}|{avatar.Icon}"),
            .. weapons.Select(weapon => $"Weapon|{(uint)weapon.Id}|{weapon.Name}|{(int)weapon.RankLevel}|{weapon.Icon}"),
            .. reliquaries.SelectMany(reliquary => reliquary.Ids.Select(id => $"Reliquary|{(uint)id}|{reliquary.Name}|{(uint)reliquary.SetId}|{(int)reliquary.RankLevel}|{reliquary.Icon}")),
            .. materials.Select(material => $"Material|{(uint)material.Id}|{material.Name}|{material.MaterialType}|{(int)material.RankLevel}|{material.Icon}"),
            .. displayItems.Select(item => $"DisplayItem|{(uint)item.Id}|{item.Name}|{(int)item.RankLevel}|{item.Icon}"),
            .. beyondItems.Select(item => $"BeyondItem|{(uint)item.Id}|{item.Name}|{item.Type}|{(int)item.RankLevel}|{item.Icon}"),
        ];

        return
        [
            CreateMetadataTableSyncState("hutao_meta_regions", lang, 6, new string[] { "cn_gf01", "cn_qd01", "os_usa", "os_euro", "os_asia", "os_cht" }),
            CreateEnumMetadataTableSyncState<ElementName>("hutao_meta_elements", lang),
            CreateEnumMetadataTableSyncState<WeaponType>("hutao_meta_weapon_types", lang),
            CreateEnumMetadataTableSyncState<EquipType>("hutao_meta_equip_types", lang),
            CreateEnumMetadataTableSyncState<FightProperty>("hutao_meta_fight_properties", lang),
            CreateEnumMetadataTableSyncState<WebGachaType>("hutao_meta_gacha_types", lang),
            CreateMetadataTableSyncState("hutao_meta_avatars", lang, avatars.Length, avatars.Select(avatar => $"{(uint)avatar.Id}|{avatar.Name}|{avatar.FetterInfo.Vision}|{(int)avatar.Weapon}|{(int)avatar.Quality}|{avatar.Icon}|{avatar.SideIcon}|{avatar.NameCard.PicturePrefix}")),
            CreateMetadataTableSyncState("hutao_meta_weapons", lang, weapons.Length, weapons.Select(weapon => $"{(uint)weapon.Id}|{weapon.Name}|{(int)weapon.WeaponType}|{(int)weapon.RankLevel}|{weapon.Icon}")),
            CreateMetadataTableSyncState("hutao_meta_reliquary_sets", lang, reliquarySets.Length, reliquarySets.Select(set => $"{(uint)set.SetId}|{set.Name}|{JsonSerializer.Serialize(set.Descriptions, JsonOptions.Default)}")),
            CreateMetadataTableSyncState("hutao_meta_reliquaries", lang, reliquaries.Sum(reliquary => reliquary.Ids.Length), reliquaries.SelectMany(reliquary => reliquary.Ids.Select(id => $"{(uint)id}|{reliquary.Name}|{(uint)reliquary.SetId}|{(int)reliquary.EquipType}|{(int)reliquary.RankLevel}|{reliquary.Icon}"))),
            CreateMetadataTableSyncState("hutao_meta_materials", lang, materials.Length, materials.Select(material => $"{(uint)material.Id}|{material.Name}|{material.MaterialType}|{(int)material.RankLevel}|{material.Icon}")),
            CreateMetadataTableSyncState("hutao_meta_items", lang, itemHashes.Length, itemHashes),
            CreateMetadataTableSyncState("hutao_meta_display_items", lang, displayItems.Length, displayItems.Select(item => $"{(uint)item.Id}|{item.Name}|{item.ItemType}|{(int)item.RankLevel}|{item.Icon}|{item.TypeDescription}")),
            CreateMetadataTableSyncState("hutao_meta_beyond_items", lang, beyondItems.Length, beyondItems.Select(item => $"{(uint)item.Id}|{item.Name}|{item.Type}|{item.TypeDescription}|{(int)item.RankLevel}|{item.Icon}")),
            CreateMetadataTableSyncState("hutao_meta_gacha_events", lang, gachaEvents.Length, gachaEvents.Select(gachaEvent => $"{gachaEvent.Name}|{gachaEvent.Version}|{gachaEvent.Order}|{gachaEvent.From.UtcDateTime:O}|{gachaEvent.To.UtcDateTime:O}|{(int)gachaEvent.Type}|{JsonSerializer.Serialize(gachaEvent.UpOrangeList, JsonOptions.Default)}|{JsonSerializer.Serialize(gachaEvent.UpPurpleList, JsonOptions.Default)}")),
            CreateMetadataTableSyncState("hutao_meta_reliquary_main_affixes", lang, reliquaryMainAffixes.Length, reliquaryMainAffixes.Select(affix => $"{(uint)affix.Id}|{(int)affix.Type}")),
            CreateMetadataTableSyncState("hutao_meta_reliquary_main_affix_levels", lang, reliquaryMainAffixLevels.Length, reliquaryMainAffixLevels.Select(level => $"{(int)level.Rank}|{level.Level}|{SerializeTypeValueCollection(level.Properties)}")),
            CreateMetadataTableSyncState("hutao_meta_reliquary_sub_affixes", lang, reliquarySubAffixes.Length, reliquarySubAffixes.Select(affix => $"{(uint)affix.Id}|{(int)affix.Type}|{affix.Value}")),
            CreateMetadataTableSyncState("hutao_meta_avatar_curves", lang, avatarCurves.Length, avatarCurves.Select(curve => $"{(uint)curve.Level}|{SerializeTypeValueCollection(curve.Curves)}")),
            CreateMetadataTableSyncState("hutao_meta_weapon_curves", lang, weaponCurves.Length, weaponCurves.Select(curve => $"{(uint)curve.Level}|{SerializeTypeValueCollection(curve.Curves)}")),
            CreateMetadataTableSyncState("hutao_wiki_avatars", lang, avatars.Length, avatars.Select(avatar => $"{(uint)avatar.Id}|{avatar.Name}|{(uint)avatar.PromoteId}|{avatar.Sort}|{(int)avatar.Body}|{(int)avatar.Quality}|{(int)avatar.Weapon}|{avatar.FetterInfo.Vision}|{avatar.Icon}|{avatar.SideIcon}|{SerializeImmutableArray(avatar.CultivationItems)}")),
            CreateMetadataTableSyncState("hutao_wiki_weapons", lang, weapons.Length, weapons.Select(weapon => $"{(uint)weapon.Id}|{weapon.Name}|{(uint)weapon.PromoteId}|{weapon.Sort}|{(int)weapon.WeaponType}|{(int)weapon.RankLevel}|{weapon.Icon}|{weapon.AwakenIcon}|{SerializeImmutableArray(weapon.CultivationItems)}")),
            CreateMetadataTableSyncState("hutao_wiki_monsters", lang, monsters.Length, monsters.Select(monster => $"{(uint)monster.Id}|{(uint)monster.DescribeId}|{monster.Name}|{monster.MonsterName}|{monster.Title}|{monster.Icon}|{(int)monster.Type}|{(int)monster.Arkhe}|{SerializeImmutableArray(monster.Drops)}")),
            CreateMetadataTableSyncState("hutao_wiki_monster_curves", lang, monsterCurves.Length, monsterCurves.Select(curve => $"{(uint)curve.Level}|{SerializeTypeValueCollection(curve.Curves)}")),
            CreateMetadataTableSyncState("hutao_meta_avatar_promotes", lang, avatarPromotes.Length, avatarPromotes.Select(promote => $"{(uint)promote.Id}|{(uint)promote.Level}|{SerializeTypeValueCollection(promote.AddProperties)}")),
            CreateMetadataTableSyncState("hutao_meta_weapon_promotes", lang, weaponPromotes.Length, weaponPromotes.Select(promote => $"{(uint)promote.Id}|{(uint)promote.Level}|{SerializeTypeValueCollection(promote.AddProperties)}")),
        ];
    }

    private static MetadataTableSyncState CreateEnumMetadataTableSyncState<TEnum>(string tableName, string lang)
        where TEnum : struct, Enum
    {
        string[] syncParts = [.. MySqlMetadataRows.CreateEnumSyncParts<TEnum>()];
        return CreateMetadataTableSyncState(tableName, lang, syncParts.Length, syncParts);
    }

    private static MetadataTableSyncState CreateMetadataTableSyncState(string tableName, string lang, long rowCount, IEnumerable<string> sourceParts)
    {
        return new(tableName, lang, ComputeMetadataHash(sourceParts), rowCount);
    }

    private static async ValueTask<bool> IsMetadataCompleteAsync(MySqlConnection connection, ImmutableArray<MetadataTableSyncState> tableStates, CancellationToken token)
    {
        foreach (MetadataTableSyncState state in tableStates)
        {
            long matched = await ExecuteScalarAsync(
                connection,
                """
                SELECT COUNT(*)
                FROM hutao_meta_sync_states
                WHERE source=@source
                  AND table_name=@table_name
                  AND lang=@lang
                  AND content_hash=@content_hash
                  AND row_count=@row_count
                  AND status='success'
                """,
                token,
                ("@source", MetadataSyncSource),
                ("@table_name", state.TableName),
                ("@lang", state.Lang),
                ("@content_hash", state.ContentHash),
                ("@row_count", state.RowCount)).ConfigureAwait(false);

            if (matched <= 0)
            {
                return false;
            }
        }

        return true;
    }

    private static async ValueTask UpsertMetaSyncStatesAsync(MySqlConnection connection, ImmutableArray<MetadataTableSyncState> tableStates, CancellationToken token)
    {
        foreach (MetadataTableSyncState state in tableStates)
        {
            await ExecuteNonQueryAsync(
                connection,
                """
                INSERT INTO hutao_meta_sync_states
                (source, table_name, lang, content_hash, row_count, status, started_at, finished_at, error_message)
                VALUES (@source, @table_name, @lang, @content_hash, @row_count, 'success', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP, NULL)
                ON DUPLICATE KEY UPDATE
                  content_hash=VALUES(content_hash),
                  row_count=VALUES(row_count),
                  status=VALUES(status),
                  started_at=VALUES(started_at),
                  finished_at=VALUES(finished_at),
                  error_message=NULL
                """,
                token,
                ("@source", MetadataSyncSource),
                ("@table_name", state.TableName),
                ("@lang", state.Lang),
                ("@content_hash", state.ContentHash),
                ("@row_count", state.RowCount)).ConfigureAwait(false);
        }
    }

    private static async ValueTask<bool> IsCoreMetadataCompleteAsync(
        MySqlConnection connection,
        string lang,
        int avatars,
        int weapons,
        int reliquaries,
        int reliquarySets,
        int materials,
        CancellationToken token)
    {
        return
            await CountLocalizedTableAsync(connection, "hutao_meta_avatars", lang, token).ConfigureAwait(false) >= avatars &&
            await CountLocalizedTableAsync(connection, "hutao_meta_weapons", lang, token).ConfigureAwait(false) >= weapons &&
            await CountLocalizedTableAsync(connection, "hutao_meta_reliquaries", lang, token).ConfigureAwait(false) >= reliquaries &&
            await CountLocalizedTableAsync(connection, "hutao_meta_reliquary_sets", lang, token).ConfigureAwait(false) >= reliquarySets &&
            await CountLocalizedTableAsync(connection, "hutao_meta_materials", lang, token).ConfigureAwait(false) >= materials;
    }

    private static async ValueTask UpsertMetaVersionAsync(MySqlConnection connection, string lang, string metadataVersion, CancellationToken token)
    {
        await ExecuteNonQueryAsync(
            connection,
            """
            INSERT INTO hutao_meta_versions (source, lang, version)
            VALUES ('SnapHutaoMetadata', @lang, @version)
            ON DUPLICATE KEY UPDATE version=VALUES(version), synced_at=CURRENT_TIMESTAMP
            """,
            token,
            ("@lang", lang),
            ("@version", metadataVersion)).ConfigureAwait(false);
    }

    private void NotifyMetadataSyncSkipped(ImmutableArray<MetadataTableSyncState> tableStates)
    {
        string summary = FormatMetadataTableSummary(tableStates);
        NotifyOnMainThread(InfoBarMessage.Success("MySQL 元数据已是最新", $"共检查 {tableStates.Length} 张表，无需写入：{summary}"));
        TraceDebug($"metadata: notification sent skipped tables={tableStates.Length}");
    }

    private void NotifyMetadataSyncCompleted(ImmutableArray<MetadataTableSyncState> tableStates)
    {
        string summary = FormatMetadataTableSummary(tableStates);
        NotifyOnMainThread(InfoBarMessage.Success("MySQL 元数据同步完成", $"已同步 {tableStates.Length} 张表：{summary}"));
        TraceDebug($"metadata: notification sent completed tables={tableStates.Length}");
    }

    private void NotifyMetadataSyncFailed()
    {
        NotifyOnMainThread(InfoBarMessage.Error("MySQL 元数据同步失败", "请查看 mysql-sync-debug.log 或后台日志定位具体原因。"));
    }

    private void NotifyOnMainThread(InfoBarMessage message)
    {
        taskContext.BeginInvokeOnMainThread(() => messenger.Send(message));
    }

    private static string FormatMetadataTableSummary(ImmutableArray<MetadataTableSyncState> tableStates)
    {
        return string.Join("、", tableStates.Select(static state => GetMetadataTableDisplayName(state.TableName)));
    }

    private static string GetMetadataTableDisplayName(string tableName)
    {
        return tableName switch
        {
            "hutao_meta_regions" => "服务器",
            "hutao_meta_elements" => "元素",
            "hutao_meta_weapon_types" => "武器类型",
            "hutao_meta_equip_types" => "圣遗物部位",
            "hutao_meta_fight_properties" => "属性词条",
            "hutao_meta_gacha_types" => "祈愿类型",
            "hutao_meta_avatars" => "角色",
            "hutao_meta_weapons" => "武器",
            "hutao_meta_reliquary_sets" => "圣遗物套装",
            "hutao_meta_reliquaries" => "圣遗物",
            "hutao_meta_materials" => "材料",
            "hutao_meta_items" => "物品索引",
            "hutao_meta_display_items" => "展示物品",
            "hutao_meta_beyond_items" => "Beyond 物品",
            "hutao_meta_gacha_events" => "卡池活动",
            "hutao_meta_reliquary_main_affixes" => "圣遗物主词条",
            "hutao_meta_reliquary_main_affix_levels" => "主词条等级",
            "hutao_meta_reliquary_sub_affixes" => "副词条",
            "hutao_meta_avatar_curves" => "角色成长曲线",
            "hutao_meta_weapon_curves" => "武器成长曲线",
            "hutao_wiki_avatars" => "角色资料",
            "hutao_wiki_weapons" => "武器资料",
            "hutao_wiki_monsters" => "怪物资料",
            "hutao_wiki_monster_curves" => "怪物成长曲线",
            "hutao_meta_avatar_promotes" => "角色突破",
            "hutao_meta_weapon_promotes" => "武器突破",
            _ => tableName,
        };
    }

    private static ValueTask<long> CountLocalizedTableAsync(MySqlConnection connection, string table, string lang, CancellationToken token)
    {
        return ExecuteScalarAsync(connection, $"SELECT COUNT(*) FROM `{table}` WHERE lang=@lang", token, ("@lang", lang));
    }

    private static ValueTask<long> CountTableAsync(MySqlConnection connection, string table, CancellationToken token)
    {
        return ExecuteScalarAsync(connection, $"SELECT COUNT(*) FROM `{table}`", token);
    }

    private static async ValueTask SyncRegionMetadataAsync(MySqlConnection connection, string lang, CancellationToken token)
    {
        (string Region, string Name, bool IsOversea)[] regions =
        [
            ("cn_gf01", "天空岛", false),
            ("cn_qd01", "世界树", false),
            ("os_usa", "America", true),
            ("os_euro", "Europe", true),
            ("os_asia", "Asia", true),
            ("os_cht", "TW, HK, MO", true),
        ];

        foreach ((string region, string name, bool isOversea) in regions)
        {
            await ExecuteNonQueryAsync(
                connection,
                """
                INSERT INTO hutao_meta_regions (region, lang, name, is_oversea, raw_json)
                VALUES (@region, @lang, @name, @is_oversea, NULL)
                ON DUPLICATE KEY UPDATE name=VALUES(name), is_oversea=VALUES(is_oversea)
                """,
                token,
                ("@region", region),
                ("@lang", lang),
                ("@name", name),
                ("@is_oversea", isOversea)).ConfigureAwait(false);
        }
    }

    private static async ValueTask SyncEnumMetadataAsync<TEnum>(MySqlConnection connection, string table, string idColumn, string lang, CancellationToken token)
        where TEnum : struct, Enum
    {
        foreach (MySqlMetadataRows.EnumRow row in MySqlMetadataRows.CreateEnumRows<TEnum>(lang))
        {
            await ExecuteNonQueryAsync(
                connection,
                $"""
                INSERT INTO {table} ({idColumn}, lang, name, raw_json)
                VALUES (@value, @lang, @name, NULL)
                ON DUPLICATE KEY UPDATE name=VALUES(name)
                """,
                token,
                ("@value", row.Value),
                ("@lang", row.Lang),
                ("@name", row.Name)).ConfigureAwait(false);
        }
    }

    private static async ValueTask SyncAvatarSkillMetadataAsync(MySqlConnection connection, MetaAvatar avatar, MetaSkill skill, int? skillType, string lang, CancellationToken token)
    {
        await ExecuteNonQueryAsync(
            connection,
            """
            INSERT INTO hutao_meta_avatar_skills
            (avatar_id, skill_id, lang, name, skill_type, icon, description, raw_json)
            VALUES (@avatar_id, @skill_id, @lang, @name, @skill_type, @icon, @description, @raw_json)
            ON DUPLICATE KEY UPDATE name=VALUES(name), skill_type=VALUES(skill_type), icon=VALUES(icon), description=VALUES(description), raw_json=VALUES(raw_json)
            """,
            token,
            ("@avatar_id", (uint)avatar.Id),
            ("@skill_id", (uint)skill.Id),
            ("@lang", lang),
            ("@name", skill.Name),
            ("@skill_type", skillType),
            ("@icon", skill.Icon),
            ("@description", skill.Description),
            ("@raw_json", TrySerializeMetadataJson(skill))).ConfigureAwait(false);

        await UpsertMetaImageAsync(connection, SkillIconCategory(skill.Icon), $"{skill.Icon}.png", token).ConfigureAwait(false);
    }

    private static async ValueTask SyncAvatarConstellationMetadataAsync(MySqlConnection connection, MetaAvatar avatar, MetaSkill talent, int position, string lang, CancellationToken token)
    {
        await ExecuteNonQueryAsync(
            connection,
            """
            INSERT INTO hutao_meta_avatar_constellations
            (avatar_id, constellation_id, lang, position, name, icon, effect, raw_json)
            VALUES (@avatar_id, @constellation_id, @lang, @position, @name, @icon, @effect, @raw_json)
            ON DUPLICATE KEY UPDATE position=VALUES(position), name=VALUES(name), icon=VALUES(icon), effect=VALUES(effect), raw_json=VALUES(raw_json)
            """,
            token,
            ("@avatar_id", (uint)avatar.Id),
            ("@constellation_id", (uint)talent.Id),
            ("@lang", lang),
            ("@position", position),
            ("@name", talent.Name),
            ("@icon", talent.Icon),
            ("@effect", talent.Description),
            ("@raw_json", TrySerializeMetadataJson(talent))).ConfigureAwait(false);

        await UpsertMetaImageAsync(connection, SkillIconCategory(talent.Icon), $"{talent.Icon}.png", token).ConfigureAwait(false);
    }

    private static async ValueTask SyncGrowCurvesAsync(MySqlConnection connection, string table, ImmutableArray<GrowCurve> curves, string lang, CancellationToken token)
    {
        foreach (GrowCurve curve in curves)
        {
            await ExecuteNonQueryAsync(
                connection,
                $"""
                INSERT INTO {table}
                (level, lang, curves_json, raw_json)
                VALUES (@level, @lang, @curves_json, @raw_json)
                ON DUPLICATE KEY UPDATE curves_json=VALUES(curves_json), raw_json=VALUES(raw_json)
                """,
                token,
                ("@level", (uint)curve.Level),
                ("@lang", lang),
                ("@curves_json", SerializeTypeValueCollection(curve.Curves)),
                ("@raw_json", TrySerializeMetadataJson(curve))).ConfigureAwait(false);
        }
    }

    private static async ValueTask SyncPromotesAsync(MySqlConnection connection, string table, ImmutableArray<Promote> promotes, string lang, CancellationToken token)
    {
        foreach (Promote promote in promotes)
        {
            await ExecuteNonQueryAsync(
                connection,
                $"""
                INSERT INTO {table}
                (promote_id, promote_level, lang, add_properties_json, raw_json)
                VALUES (@promote_id, @promote_level, @lang, @add_properties_json, @raw_json)
                ON DUPLICATE KEY UPDATE add_properties_json=VALUES(add_properties_json), raw_json=VALUES(raw_json)
                """,
                token,
                ("@promote_id", (uint)promote.Id),
                ("@promote_level", (uint)promote.Level),
                ("@lang", lang),
                ("@add_properties_json", SerializeTypeValueCollection(promote.AddProperties)),
                ("@raw_json", TrySerializeMetadataJson(promote))).ConfigureAwait(false);
        }
    }

    private static async ValueTask UpsertMetaDisplayItemAsync(MySqlConnection connection, DisplayItem item, string lang, CancellationToken token)
    {
        await ExecuteNonQueryAsync(
            connection,
            """
            INSERT INTO hutao_meta_display_items
            (item_id, lang, name, item_type, rank_level, sort_rank, icon, description, type_description, raw_json)
            VALUES (@id, @lang, @name, @item_type, @rank_level, @sort_rank, @icon, @description, @type_description, @raw_json)
            ON DUPLICATE KEY UPDATE name=VALUES(name), item_type=VALUES(item_type), rank_level=VALUES(rank_level), sort_rank=VALUES(sort_rank),
            icon=VALUES(icon), description=VALUES(description), type_description=VALUES(type_description), raw_json=VALUES(raw_json)
            """,
            token,
            ("@id", (uint)item.Id),
            ("@lang", lang),
            ("@name", item.Name),
            ("@item_type", item.ItemType.ToString()),
            ("@rank_level", (int)item.RankLevel),
            ("@sort_rank", item.Rank),
            ("@icon", item.Icon),
            ("@description", item.Description),
            ("@type_description", item.TypeDescription),
            ("@raw_json", TrySerializeMetadataJson(item))).ConfigureAwait(false);
    }

    private static async ValueTask EnsureMetadataTablesAsync(MySqlConnection connection, CancellationToken token)
    {
        await EnsureImageTableAsync(connection, token).ConfigureAwait(false);

        await ExecuteNonQueryAsync(
            connection,
            """
            CREATE TABLE IF NOT EXISTS hutao_meta_sync_states (
              source VARCHAR(64) NOT NULL COMMENT '元数据来源，例如 SnapHutaoMetadata',
              table_name VARCHAR(128) NOT NULL COMMENT 'MySQL 目标表名',
              lang VARCHAR(16) NOT NULL COMMENT '语言，例如 zh-cn',
              content_hash CHAR(64) NOT NULL COMMENT '当前本地元数据内容 SHA-256',
              row_count BIGINT NOT NULL COMMENT '当前元数据预计写入行数',
              status VARCHAR(16) NOT NULL COMMENT '同步状态：success/failed',
              started_at DATETIME NOT NULL COMMENT '本表本次同步开始时间',
              finished_at DATETIME NULL COMMENT '本表本次同步完成时间',
              error_message TEXT NULL COMMENT '本表最近一次同步失败原因',
              updated_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP COMMENT '记录更新时间',
              PRIMARY KEY (source, table_name, lang),
              INDEX idx_hutao_meta_sync_states_status (status),
              INDEX idx_hutao_meta_sync_states_finished_at (finished_at)
            ) COMMENT='元数据逐表同步状态'
            """,
            token).ConfigureAwait(false);

        await ExecuteNonQueryAsync(
            connection,
            """
            CREATE TABLE IF NOT EXISTS hutao_meta_display_items (
              item_id BIGINT UNSIGNED NOT NULL COMMENT '展示物品 ID',
              lang VARCHAR(16) NOT NULL COMMENT '语言',
              name VARCHAR(128) NOT NULL COMMENT '展示物品名称',
              item_type VARCHAR(64) NULL COMMENT '物品类型',
              rank_level INT NULL COMMENT '品质或星级',
              sort_rank INT NULL COMMENT '排序或品质补充值',
              icon VARCHAR(512) NULL COMMENT '图标资源名',
              description TEXT NULL COMMENT '描述',
              type_description VARCHAR(128) NULL COMMENT '类型描述',
              raw_json JSON NULL COMMENT '完整原始元数据 JSON',
              PRIMARY KEY (item_id, lang),
              INDEX idx_hutao_meta_display_items_name (name)
            ) COMMENT='展示物品字典'
            """,
            token).ConfigureAwait(false);

        await ExecuteNonQueryAsync(
            connection,
            """
            CREATE TABLE IF NOT EXISTS hutao_meta_beyond_items (
              item_id BIGINT UNSIGNED NOT NULL COMMENT '幽境危战等 Beyond 物品 ID',
              lang VARCHAR(16) NOT NULL COMMENT '语言',
              name VARCHAR(128) NOT NULL COMMENT '物品名称',
              type INT NULL COMMENT '物品类型枚举值',
              type_description VARCHAR(128) NULL COMMENT '类型描述',
              rank_level INT NULL COMMENT '品质或星级',
              icon VARCHAR(512) NULL COMMENT '图标资源名',
              description TEXT NULL COMMENT '描述',
              raw_json JSON NULL COMMENT '完整原始元数据 JSON',
              PRIMARY KEY (item_id, lang),
              INDEX idx_hutao_meta_beyond_items_name (name)
            ) COMMENT='Beyond 物品字典'
            """,
            token).ConfigureAwait(false);

        await ExecuteNonQueryAsync(
            connection,
            """
            CREATE TABLE IF NOT EXISTS hutao_meta_gacha_events (
              name VARCHAR(256) NOT NULL COMMENT '卡池名称',
              lang VARCHAR(16) NOT NULL COMMENT '语言',
              version VARCHAR(32) NOT NULL COMMENT '游戏版本',
              sort_order INT UNSIGNED NOT NULL COMMENT '排序值',
              banner_url VARCHAR(1024) NOT NULL COMMENT '卡池横幅图片 URL',
              banner2_url VARCHAR(1024) NULL COMMENT '第二横幅图片 URL',
              from_time DATETIME NOT NULL COMMENT '开始时间',
              to_time DATETIME NOT NULL COMMENT '结束时间',
              gacha_type INT NOT NULL COMMENT '卡池类型枚举值',
              up_orange_json JSON NOT NULL COMMENT 'UP 五星物品 ID 列表',
              up_purple_json JSON NOT NULL COMMENT 'UP 四星物品 ID 列表',
              raw_json JSON NULL COMMENT '完整原始元数据 JSON',
              PRIMARY KEY (name, lang, from_time),
              INDEX idx_hutao_meta_gacha_events_type_time (gacha_type, from_time, to_time)
            ) COMMENT='祈愿卡池事件字典'
            """,
            token).ConfigureAwait(false);

        await ExecuteNonQueryAsync(
            connection,
            """
            CREATE TABLE IF NOT EXISTS hutao_meta_reliquary_main_affixes (
              affix_id BIGINT UNSIGNED NOT NULL COMMENT '圣遗物主词条 ID',
              lang VARCHAR(16) NOT NULL COMMENT '语言',
              property_type INT NOT NULL COMMENT '战斗属性枚举值',
              raw_json JSON NULL COMMENT '完整原始元数据 JSON',
              PRIMARY KEY (affix_id, lang)
            ) COMMENT='圣遗物主词条字典'
            """,
            token).ConfigureAwait(false);

        await ExecuteNonQueryAsync(
            connection,
            """
            CREATE TABLE IF NOT EXISTS hutao_meta_reliquary_main_affix_levels (
              rank_level INT NOT NULL COMMENT '圣遗物星级',
              level INT UNSIGNED NOT NULL COMMENT '圣遗物等级',
              lang VARCHAR(16) NOT NULL COMMENT '语言',
              properties_json JSON NOT NULL COMMENT '主词条属性值 JSON',
              raw_json JSON NULL COMMENT '完整原始元数据 JSON',
              PRIMARY KEY (rank_level, level, lang)
            ) COMMENT='圣遗物主词条等级数值表'
            """,
            token).ConfigureAwait(false);

        await ExecuteNonQueryAsync(
            connection,
            """
            CREATE TABLE IF NOT EXISTS hutao_meta_reliquary_sub_affixes (
              affix_id BIGINT UNSIGNED NOT NULL COMMENT '圣遗物副词条 ID',
              lang VARCHAR(16) NOT NULL COMMENT '语言',
              property_type INT NOT NULL COMMENT '战斗属性枚举值',
              affix_value DOUBLE NOT NULL COMMENT '副词条单次强化值',
              raw_json JSON NULL COMMENT '完整原始元数据 JSON',
              PRIMARY KEY (affix_id, lang)
            ) COMMENT='圣遗物副词条字典'
            """,
            token).ConfigureAwait(false);

        await EnsureCurveTableAsync(connection, "hutao_meta_avatar_curves", "角色成长曲线表", token).ConfigureAwait(false);
        await EnsureCurveTableAsync(connection, "hutao_meta_weapon_curves", "武器成长曲线表", token).ConfigureAwait(false);
        await EnsureWikiProfileTablesAsync(connection, token).ConfigureAwait(false);
        await EnsureCurveTableAsync(connection, "hutao_wiki_monster_curves", "怪物成长曲线表", token).ConfigureAwait(false);
        await EnsurePromoteTableAsync(connection, "hutao_meta_avatar_promotes", "角色突破加成表", token).ConfigureAwait(false);
        await EnsurePromoteTableAsync(connection, "hutao_meta_weapon_promotes", "武器突破加成表", token).ConfigureAwait(false);
    }

    private static async ValueTask EnsureWikiProfileTablesAsync(MySqlConnection connection, CancellationToken token)
    {
        await ExecuteNonQueryAsync(
            connection,
            """
            CREATE TABLE IF NOT EXISTS hutao_wiki_avatars (
              avatar_id BIGINT UNSIGNED NOT NULL COMMENT '角色 ID',
              lang VARCHAR(16) NOT NULL COMMENT '语言',
              promote_id BIGINT UNSIGNED NOT NULL COMMENT '角色突破组 ID',
              sort_order INT UNSIGNED NOT NULL COMMENT '官方排序值',
              body_type INT NOT NULL COMMENT '体型枚举值',
              name VARCHAR(128) NOT NULL COMMENT '角色名称',
              description TEXT NOT NULL COMMENT '角色描述',
              begin_time DATETIME NOT NULL COMMENT '角色上线时间',
              quality INT NOT NULL COMMENT '角色星级',
              weapon_type INT NOT NULL COMMENT '武器类型枚举值',
              element VARCHAR(64) NULL COMMENT '元素名称',
              icon VARCHAR(512) NOT NULL COMMENT '头像资源名',
              side_icon VARCHAR(512) NOT NULL COMMENT '侧头像资源名',
              base_value_json JSON NOT NULL COMMENT '角色基础属性 JSON',
              grow_curves_json JSON NOT NULL COMMENT '角色成长曲线引用 JSON',
              skill_depot_json JSON NOT NULL COMMENT '技能库完整 JSON',
              fetter_info_json JSON NOT NULL COMMENT '角色资料与元素信息 JSON',
              costumes_json JSON NOT NULL COMMENT '衣装列表 JSON',
              cultivation_items_json JSON NOT NULL COMMENT '养成材料 ID 列表 JSON',
              name_card_json JSON NOT NULL COMMENT '名片信息 JSON',
              raw_json JSON NULL COMMENT '完整角色资料原始 JSON',
              PRIMARY KEY (avatar_id, lang),
              INDEX idx_hutao_wiki_avatars_name (name),
              INDEX idx_hutao_wiki_avatars_element (element)
            ) COMMENT='角色资料页完整公开资料'
            """,
            token).ConfigureAwait(false);

        await ExecuteNonQueryAsync(
            connection,
            """
            CREATE TABLE IF NOT EXISTS hutao_wiki_weapons (
              weapon_id BIGINT UNSIGNED NOT NULL COMMENT '武器 ID',
              lang VARCHAR(16) NOT NULL COMMENT '语言',
              promote_id BIGINT UNSIGNED NOT NULL COMMENT '武器突破组 ID',
              sort_order INT UNSIGNED NOT NULL COMMENT '官方排序值',
              weapon_type INT NOT NULL COMMENT '武器类型枚举值',
              quality INT NOT NULL COMMENT '武器星级',
              name VARCHAR(128) NOT NULL COMMENT '武器名称',
              description TEXT NOT NULL COMMENT '武器描述',
              icon VARCHAR(512) NOT NULL COMMENT '图标资源名',
              awaken_icon VARCHAR(512) NOT NULL COMMENT '精炼或觉醒图标资源名',
              grow_curves_json JSON NOT NULL COMMENT '武器成长曲线引用 JSON',
              affix_json JSON NULL COMMENT '武器特效 JSON',
              cultivation_items_json JSON NOT NULL COMMENT '养成材料 ID 列表 JSON',
              raw_json JSON NULL COMMENT '完整武器资料原始 JSON',
              PRIMARY KEY (weapon_id, lang),
              INDEX idx_hutao_wiki_weapons_name (name),
              INDEX idx_hutao_wiki_weapons_type_quality (weapon_type, quality)
            ) COMMENT='武器资料页完整公开资料'
            """,
            token).ConfigureAwait(false);

        await ExecuteNonQueryAsync(
            connection,
            """
            CREATE TABLE IF NOT EXISTS hutao_wiki_monsters (
              monster_id BIGINT UNSIGNED NOT NULL COMMENT '怪物 ID',
              describe_id BIGINT UNSIGNED NOT NULL COMMENT '怪物描述 ID，深渊等场景会按此 ID 归一',
              lang VARCHAR(16) NOT NULL COMMENT '语言',
              monster_name VARCHAR(128) NULL COMMENT '怪物内部或显示名称',
              name VARCHAR(128) NULL COMMENT '怪物名称',
              title VARCHAR(256) NULL COMMENT '怪物标题',
              description TEXT NULL COMMENT '怪物描述',
              icon VARCHAR(512) NOT NULL COMMENT '怪物图标资源名',
              monster_type INT NOT NULL COMMENT '怪物类型枚举值',
              arkhe INT NOT NULL COMMENT '始基力枚举值',
              affixes_json JSON NULL COMMENT '怪物词缀 JSON',
              drops_json JSON NOT NULL COMMENT '掉落材料 ID 列表 JSON',
              base_value_json JSON NULL COMMENT '怪物基础属性 JSON',
              grow_curves_json JSON NULL COMMENT '怪物成长曲线引用 JSON',
              raw_json JSON NULL COMMENT '完整怪物资料原始 JSON',
              PRIMARY KEY (monster_id, describe_id, lang),
              INDEX idx_hutao_wiki_monsters_describe_id (describe_id),
              INDEX idx_hutao_wiki_monsters_name (name),
              INDEX idx_hutao_wiki_monsters_type (monster_type)
            ) COMMENT='怪物资料页完整公开资料'
            """,
            token).ConfigureAwait(false);

        await EnsureWikiMonsterPrimaryKeyAsync(connection, token).ConfigureAwait(false);
    }

    private static async ValueTask EnsureWikiMonsterPrimaryKeyAsync(MySqlConnection connection, CancellationToken token)
    {
        long currentKeyColumns = await ExecuteScalarAsync(
            connection,
            """
            SELECT COUNT(*)
            FROM INFORMATION_SCHEMA.STATISTICS
            WHERE TABLE_SCHEMA = DATABASE()
              AND TABLE_NAME = 'hutao_wiki_monsters'
              AND INDEX_NAME = 'PRIMARY'
              AND COLUMN_NAME IN ('monster_id', 'describe_id', 'lang')
            """,
            token).ConfigureAwait(false);

        if (currentKeyColumns is 3)
        {
            return;
        }

        await ExecuteNonQueryAsync(
            connection,
            """
            ALTER TABLE hutao_wiki_monsters
              DROP PRIMARY KEY,
              ADD PRIMARY KEY (monster_id, describe_id, lang)
            """,
            token).ConfigureAwait(false);
    }

    private static async ValueTask EnsureCurveTableAsync(MySqlConnection connection, string table, string comment, CancellationToken token)
    {
        await ExecuteNonQueryAsync(
            connection,
            $"""
            CREATE TABLE IF NOT EXISTS {table} (
              level INT UNSIGNED NOT NULL COMMENT '等级',
              lang VARCHAR(16) NOT NULL COMMENT '语言',
              curves_json JSON NOT NULL COMMENT '成长曲线 JSON',
              raw_json JSON NULL COMMENT '完整原始元数据 JSON',
              PRIMARY KEY (level, lang)
            ) COMMENT='{comment}'
            """,
            token).ConfigureAwait(false);
    }

    private static async ValueTask EnsurePromoteTableAsync(MySqlConnection connection, string table, string comment, CancellationToken token)
    {
        await ExecuteNonQueryAsync(
            connection,
            $"""
            CREATE TABLE IF NOT EXISTS {table} (
              promote_id BIGINT UNSIGNED NOT NULL COMMENT '突破组 ID',
              promote_level INT UNSIGNED NOT NULL COMMENT '突破等级',
              lang VARCHAR(16) NOT NULL COMMENT '语言',
              add_properties_json JSON NOT NULL COMMENT '突破属性加成 JSON',
              raw_json JSON NULL COMMENT '完整原始元数据 JSON',
              PRIMARY KEY (promote_id, promote_level, lang)
            ) COMMENT='{comment}'
            """,
            token).ConfigureAwait(false);
    }

    private static async ValueTask EnsureImageTableAsync(MySqlConnection connection, CancellationToken token)
    {
        await ExecuteNonQueryAsync(
            connection,
            """
            CREATE TABLE IF NOT EXISTS hutao_meta_images (
              id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT COMMENT '自增主键',
              category VARCHAR(64) NOT NULL COMMENT '图片分类，如 AvatarIcon、ItemIcon、EquipIcon、RelicIcon、Skill、Talent、NameCardPic、DailyNoteAvatarSideIcon',
              resource_name VARCHAR(255) NOT NULL COMMENT '资源文件名，如 UI_AvatarIcon_Ambor.png',
              resource_url VARCHAR(1024) NOT NULL COMMENT '胡桃静态资源原始 URL，可直接给前端兜底展示',
              content_type VARCHAR(128) NULL COMMENT '图片 MIME 类型，如 image/png',
              content_length INT UNSIGNED NULL COMMENT '图片字节数',
              synced_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP COMMENT '同步时间',
              PRIMARY KEY (id),
              UNIQUE KEY uk_hutao_meta_images_category_resource (category, resource_name),
              KEY idx_hutao_meta_images_category (category)
            ) COMMENT='胡桃元数据图片表'
            """,
            token).ConfigureAwait(false);

        if (await ExecuteScalarAsync(
            connection,
            """
            SELECT COUNT(*)
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE()
              AND TABLE_NAME = 'hutao_meta_images'
              AND COLUMN_NAME = 'content_bytes'
            """,
            token).ConfigureAwait(false) > 0)
        {
            await ExecuteNonQueryAsync(connection, "ALTER TABLE hutao_meta_images DROP COLUMN content_bytes", token).ConfigureAwait(false);
        }
    }

    private static async ValueTask UpsertMetaImageAsync(MySqlConnection connection, string category, string resourceName, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(category) || string.IsNullOrWhiteSpace(resourceName) || resourceName is ".png")
        {
            return;
        }

        string url = StaticResourcesEndpoints.StaticRaw(category, resourceName);
        await UpsertMetaImageByUrlAsync(connection, category, resourceName, url, token).ConfigureAwait(false);
    }

    private static async ValueTask UpsertMetaImageByUrlAsync(MySqlConnection connection, string category, string resourceUrl, CancellationToken token)
    {
        if (!Uri.TryCreate(resourceUrl, UriKind.Absolute, out Uri? uri))
        {
            return;
        }

        await UpsertMetaImageByUrlAsync(connection, category, Path.GetFileName(uri.LocalPath), resourceUrl, token).ConfigureAwait(false);
    }

    private static async ValueTask UpsertMetaImageByUrlAsync(MySqlConnection connection, string category, string resourceName, string url, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(category) || string.IsNullOrWhiteSpace(resourceName))
        {
            return;
        }

        await ExecuteNonQueryAsync(
            connection,
            """
            INSERT INTO hutao_meta_images (category, resource_name, resource_url)
            VALUES (@category, @resource_name, @resource_url)
            ON DUPLICATE KEY UPDATE resource_url=VALUES(resource_url)
            """,
            token,
            ("@category", category),
            ("@resource_name", resourceName),
            ("@resource_url", url)).ConfigureAwait(false);

        // Store image references only. Binary image content belongs in static storage/CDN, not MySQL.
    }

    private static string SkillIconCategory(string icon)
    {
        return icon.StartsWith("UI_Talent_", StringComparison.Ordinal) ? "Talent" : "Skill";
    }

    private static async ValueTask UpsertMetaItemAsync(MySqlConnection connection, uint id, string lang, string name, string itemType, int rankLevel, string icon, string? description, object raw, CancellationToken token)
    {
        await ExecuteNonQueryAsync(
            connection,
            """
            INSERT INTO hutao_meta_items
            (item_id, lang, name, item_type, rank_level, icon, description, raw_json)
            VALUES (@id, @lang, @name, @item_type, @rank_level, @icon, @description, @raw_json)
            ON DUPLICATE KEY UPDATE name=VALUES(name), item_type=VALUES(item_type), rank_level=VALUES(rank_level),
            icon=VALUES(icon), description=VALUES(description), raw_json=VALUES(raw_json)
            """,
            token,
            ("@id", id),
            ("@lang", lang),
            ("@name", name),
            ("@item_type", itemType),
            ("@rank_level", rankLevel),
            ("@icon", icon),
            ("@description", description),
            ("@raw_json", TrySerializeMetadataJson(raw))).ConfigureAwait(false);
    }

    private static async ValueTask UpsertAccountAsync(MySqlConnection connection, string uid, string? nickname, CancellationToken token)
    {
        await ExecuteNonQueryAsync(
            connection,
            """
            INSERT INTO hutao_accounts (uid, region, nickname)
            VALUES (@uid, @region, @nickname)
            ON DUPLICATE KEY UPDATE region=VALUES(region), nickname=COALESCE(VALUES(nickname), nickname), updated_at=CURRENT_TIMESTAMP
            """,
            token,
            ("@uid", uid),
            ("@region", Region.UnsafeFromUidString(uid).Value),
            ("@nickname", nickname)).ConfigureAwait(false);
    }

    private static string SerializeTypeValueCollection<TType, TValue>(TypeValueCollection<TType, TValue> collection)
        where TType : notnull
    {
        return JsonSerializer.Serialize(
            collection.Array.Select(entry => new
            {
                Type = ConvertMetadataJsonScalar(entry.Type),
                TypeName = entry.Type.ToString(),
                Value = entry.Value,
            }),
            JsonOptions.Default);
    }

    private static object ConvertMetadataJsonScalar<TValue>(TValue value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        Type type = Nullable.GetUnderlyingType(typeof(TValue)) ?? typeof(TValue);
        return type.IsEnum ? Convert.ToInt64(value) : value;
    }

    private static string SerializeImmutableArray<T>(ImmutableArray<T> array)
    {
        return JsonSerializer.Serialize(array.IsDefault ? [] : array, JsonOptions.Default);
    }

    private static string SerializeMetadataJsonOrNullLiteral(object? value)
    {
        return TrySerializeMetadataJson(value) ?? "null";
    }

    private static string ComputeMetadataHash(IEnumerable<string> sourceParts)
    {
        string content = string.Join('\u001F', sourceParts.Order(StringComparer.Ordinal));
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes($"{MetadataSyncSchemaVersion}\u001E{content}");
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
    }

    private static string? TrySerializeMetadataJson(object? value)
    {
        if (value is null)
        {
            return default;
        }

        try
        {
            return JsonSerializer.Serialize(value, JsonOptions.Default);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            return default;
        }
    }

    private async ValueTask<bool> ExecuteAsync(string scope, Func<MySqlConnection, ValueTask> action, CancellationToken token)
    {
        MySqlSyncOptions? options = MySqlSyncOptions.FromEnvironment();
        if (options is null)
        {
            TraceDebug($"{scope}: skip because HUTAO_MYSQL_CONNECTION_STRING is empty");
            NotifyDataSyncSkipped(scope);
            return false;
        }

        try
        {
            TraceDebug($"{scope}: opening MySQL connection");
            await using MySqlConnection connection = new(options.ConnectionString);
            await connection.OpenAsync(token).ConfigureAwait(false);
            TraceDebug($"{scope}: opened MySQL connection");
            await action(connection).ConfigureAwait(false);
            TraceDebug($"{scope}: synced");
            NotifyDataSyncCompleted(scope);
            return true;
        }
        catch (Exception ex)
        {
            TraceDebug($"{scope}: failed {ex.GetType().Name}: {ex.Message}");
            logger.LogWarning(ex, "Failed to sync data to MySQL");
            NotifyDataSyncFailed(scope);
            return false;
        }
    }

    private void NotifyDataSyncSkipped(string scope)
    {
        if (scope is "metadata")
        {
            return;
        }

        NotifyOnMainThread(InfoBarMessage.Information($"MySQL {GetDataSyncScopeDisplayName(scope)}同步未执行：未配置数据库连接。"));
    }

    private void NotifyDataSyncCompleted(string scope)
    {
        if (scope is "metadata")
        {
            return;
        }

        NotifyOnMainThread(InfoBarMessage.Success("MySQL 同步完成", $"{GetDataSyncScopeDisplayName(scope)}已成功同步到数据库。"));
    }

    private void NotifyDataSyncFailed(string scope)
    {
        if (scope is "metadata")
        {
            return;
        }

        NotifyOnMainThread(InfoBarMessage.Error("MySQL 同步失败", $"{GetDataSyncScopeDisplayName(scope)}同步失败，请查看 mysql-sync-debug.log 或后台日志。"));
    }

    private static string GetDataSyncScopeDisplayName(string scope)
    {
        return scope switch
        {
            "avatars" => "我的角色",
            "backpack" => "背包物品",
            "gacha" => "祈愿记录",
            "daily-note" => "实时便笺",
            _ => scope,
        };
    }

    private static async ValueTask ExecuteNonQueryAsync(MySqlConnection connection, string commandText, CancellationToken token, params (string Name, object? Value)[] parameters)
    {
        await using MySqlCommand command = new(commandText, connection);
        foreach ((string name, object? value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private static async ValueTask<long> ExecuteScalarAsync(MySqlConnection connection, string commandText, CancellationToken token, params (string Name, object? Value)[] parameters)
    {
        await using MySqlCommand command = new(commandText, connection);
        foreach ((string name, object? parameterValue) in parameters)
        {
            command.Parameters.AddWithValue(name, parameterValue ?? DBNull.Value);
        }

        object? scalar = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
        return Convert.ToInt64(scalar);
    }

    private static void TraceDebug(string message)
    {
        try
        {
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SnapHutaoRemastered");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "mysql-sync-debug.log"), $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }
}
