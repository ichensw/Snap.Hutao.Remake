// Copyright (c) DGP Studio. All rights reserved.
// Licensed under the MIT license.

using MySqlConnector;
using Snap.Hutao.Remastered.Core.Text.Json;
using Snap.Hutao.Remastered.Model.Intrinsic;
using Snap.Hutao.Remastered.Model.Entity;
using Snap.Hutao.Remastered.Model.Metadata.Item;
using Snap.Hutao.Remastered.Service.Metadata;
using Snap.Hutao.Remastered.Web.Hoyolab.Takumi.GameRecord.Avatar;
using System.Collections.Immutable;
using DailyNoteExpedition = Snap.Hutao.Remastered.Web.Hoyolab.Takumi.GameRecord.DailyNote.Expedition;
using EntityAvatarInfo = Snap.Hutao.Remastered.Model.Entity.AvatarInfo;
using MetaAvatar = Snap.Hutao.Remastered.Model.Metadata.Avatar.Avatar;
using MetaReliquary = Snap.Hutao.Remastered.Model.Metadata.Reliquary.Reliquary;
using MetaReliquarySet = Snap.Hutao.Remastered.Model.Metadata.Reliquary.ReliquarySet;
using MetaSkill = Snap.Hutao.Remastered.Model.Metadata.Avatar.Skill;
using MetaWeapon = Snap.Hutao.Remastered.Model.Metadata.Weapon.Weapon;
using WebReliquary = Snap.Hutao.Remastered.Web.Hoyolab.Takumi.GameRecord.Avatar.Reliquary;
using WebGachaType = Snap.Hutao.Remastered.Web.Hoyolab.Hk4e.Event.GachaInfo.GachaType;

namespace Snap.Hutao.Remastered.Service.MySqlSync;

[Service(ServiceLifetime.Singleton)]
public sealed class MySqlSyncService
{
    private readonly ILogger<MySqlSyncService> logger;
    private readonly IMetadataService metadataService;
    private Task? metadataSyncTask;
    private bool metadataSynced;

    public MySqlSyncService(ILogger<MySqlSyncService> logger, IMetadataService metadataService)
    {
        this.logger = logger;
        this.metadataService = metadataService;
    }

    public async ValueTask SyncAvatarInfosAsync(string uid, IEnumerable<EntityAvatarInfo> avatarInfos, CancellationToken token = default)
    {
        await EnsureMetadataSyncedAsync(token).ConfigureAwait(false);

        await ExecuteAsync(async connection =>
        {
            await ExecuteNonQueryAsync(connection, "DELETE FROM hutao_avatar_relics WHERE uid=@uid", token, ("@uid", uid)).ConfigureAwait(false);
            await ExecuteNonQueryAsync(connection, "DELETE FROM hutao_avatar_skills WHERE uid=@uid", token, ("@uid", uid)).ConfigureAwait(false);
            await ExecuteNonQueryAsync(connection, "DELETE FROM hutao_avatar_constellations WHERE uid=@uid", token, ("@uid", uid)).ConfigureAwait(false);

            foreach (EntityAvatarInfo info in avatarInfos)
            {
                if (info.Info2 is not { } detail)
                {
                    continue;
                }

                Character avatar = detail.Base;
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
                    ("@avatar_id", (uint)avatar.Id),
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
                        ("@avatar_id", (uint)avatar.Id),
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
                        ("@avatar_id", (uint)avatar.Id),
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
                        ("@avatar_id", (uint)avatar.Id),
                        ("@position", constellation.Position),
                        ("@skill_id", (uint)constellation.Id),
                        ("@name", constellation.Name),
                        ("@is_actived", constellation.IsActived),
                        ("@raw_json", JsonSerializer.Serialize(constellation, JsonOptions.Default))).ConfigureAwait(false);
                }
            }
        }, token).ConfigureAwait(false);
    }

    public async ValueTask SyncBackpackAsync(string uid, BackpackArchive archive, IEnumerable<BackpackItem> items, CancellationToken token = default)
    {
        await EnsureMetadataSyncedAsync(token).ConfigureAwait(false);

        await ExecuteAsync(async connection =>
        {
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

    public async ValueTask SyncGachaArchiveAsync(GachaArchive archive, IEnumerable<GachaItem> items, IEnumerable<BeyondGachaItem> beyondItems, CancellationToken token = default)
    {
        await EnsureMetadataSyncedAsync(token).ConfigureAwait(false);

        string uid = archive.Uid;
        await ExecuteAsync(async connection =>
        {
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
            return;
        }

        await EnsureMetadataSyncedAsync(token).ConfigureAwait(false);

        await ExecuteAsync(async connection =>
        {
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

    private async ValueTask EnsureMetadataSyncedAsync(CancellationToken token)
    {
        if (metadataSynced)
        {
            return;
        }

        metadataSyncTask ??= SyncMetadataOnceAsync(token);
        await metadataSyncTask.ConfigureAwait(false);
    }

    private async Task SyncMetadataOnceAsync(CancellationToken token)
    {
        if (!await metadataService.InitializeAsync().ConfigureAwait(false))
        {
            return;
        }

        await SyncMetadataAsync("zh-cn", token).ConfigureAwait(false);
        metadataSynced = true;
    }

    private async ValueTask SyncMetadataAsync(string lang, CancellationToken token)
    {
        ImmutableArray<MetaAvatar> avatars = await metadataService.GetAvatarArrayAsync(token).ConfigureAwait(false);
        ImmutableArray<MetaWeapon> weapons = await metadataService.GetWeaponArrayAsync(token).ConfigureAwait(false);
        ImmutableArray<MetaReliquary> reliquaries = await metadataService.GetReliquaryArrayAsync(token).ConfigureAwait(false);
        ImmutableArray<MetaReliquarySet> reliquarySets = await metadataService.GetReliquarySetArrayAsync(token).ConfigureAwait(false);
        ImmutableArray<Material> materials = await metadataService.GetMaterialArrayAsync(token).ConfigureAwait(false);

        await ExecuteAsync(async connection =>
        {
            await ExecuteNonQueryAsync(
                connection,
                """
                INSERT INTO hutao_meta_versions (source, lang, version)
                VALUES ('SnapHutaoMetadata', @lang, NULL)
                ON DUPLICATE KEY UPDATE synced_at=CURRENT_TIMESTAMP
                """,
                token,
                ("@lang", lang)).ConfigureAwait(false);

            await SyncRegionMetadataAsync(connection, lang, token).ConfigureAwait(false);
            await SyncEnumMetadataAsync<ElementName>(connection, "hutao_meta_elements", "element", lang, token).ConfigureAwait(false);
            await SyncEnumMetadataAsync<WeaponType>(connection, "hutao_meta_weapon_types", "weapon_type", lang, token).ConfigureAwait(false);
            await SyncEnumMetadataAsync<EquipType>(connection, "hutao_meta_equip_types", "equip_type", lang, token).ConfigureAwait(false);
            await SyncEnumMetadataAsync<FightProperty>(connection, "hutao_meta_fight_properties", "property_type", lang, token).ConfigureAwait(false);
            await SyncEnumMetadataAsync<WebGachaType>(connection, "hutao_meta_gacha_types", "gacha_type", lang, token).ConfigureAwait(false);

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
                    ("@raw_json", JsonSerializer.Serialize(avatar, JsonOptions.Default))).ConfigureAwait(false);

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
                    ("@raw_json", JsonSerializer.Serialize(weapon, JsonOptions.Default))).ConfigureAwait(false);

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
                    ("@raw_json", JsonSerializer.Serialize(set, JsonOptions.Default))).ConfigureAwait(false);
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
                        ("@raw_json", JsonSerializer.Serialize(reliquary, JsonOptions.Default))).ConfigureAwait(false);

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
                    ("@raw_json", JsonSerializer.Serialize(material, JsonOptions.Default))).ConfigureAwait(false);

                await UpsertMetaItemAsync(connection, (uint)material.Id, lang, material.Name, "Material", (int)material.RankLevel, material.Icon, material.Description, material, token).ConfigureAwait(false);
            }
        }, token).ConfigureAwait(false);
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
            ("@raw_json", JsonSerializer.Serialize(skill, JsonOptions.Default))).ConfigureAwait(false);
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
            ("@raw_json", JsonSerializer.Serialize(talent, JsonOptions.Default))).ConfigureAwait(false);
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
            ("@raw_json", JsonSerializer.Serialize(raw, JsonOptions.Default))).ConfigureAwait(false);
    }

    private async ValueTask ExecuteAsync(Func<MySqlConnection, ValueTask> action, CancellationToken token)
    {
        MySqlSyncOptions? options = MySqlSyncOptions.FromEnvironment();
        if (options is null)
        {
            return;
        }

        try
        {
            await using MySqlConnection connection = new(options.ConnectionString);
            await connection.OpenAsync(token).ConfigureAwait(false);
            await action(connection).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to sync data to MySQL");
        }
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
}
