CREATE DATABASE IF NOT EXISTS snap_hutao_sync
  DEFAULT CHARACTER SET utf8mb4
  DEFAULT COLLATE utf8mb4_unicode_ci;

USE snap_hutao_sync;

CREATE TABLE IF NOT EXISTS hutao_accounts (
  uid VARCHAR(32) PRIMARY KEY COMMENT '游戏 UID',
  region VARCHAR(32) NULL COMMENT '服务器区域，例如 cn_gf01',
  nickname VARCHAR(128) NULL COMMENT '游戏昵称',
  updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP COMMENT '账号信息更新时间'
) COMMENT='原神账号基础信息';

CREATE TABLE IF NOT EXISTS hutao_avatars (
  uid VARCHAR(32) NOT NULL COMMENT '游戏 UID',
  avatar_id BIGINT UNSIGNED NOT NULL COMMENT '角色 ID',
  name VARCHAR(128) NULL COMMENT '角色名称',
  element VARCHAR(32) NULL COMMENT '元素类型',
  level INT NULL COMMENT '角色等级',
  rarity INT NULL COMMENT '角色星级',
  fetter INT NULL COMMENT '好感等级',
  constellation_num INT NULL COMMENT '已激活命座数量',
  promote_level INT NULL COMMENT '角色突破等级',
  weapon_id BIGINT UNSIGNED NULL COMMENT '装备武器 ID',
  weapon_level INT NULL COMMENT '武器等级',
  weapon_affix_level INT NULL COMMENT '武器精炼等级',
  raw_json JSON NOT NULL COMMENT 'DetailedCharacter 完整原始 JSON',
  synced_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP COMMENT '同步时间',
  PRIMARY KEY (uid, avatar_id),
  INDEX idx_avatars_avatar_id (avatar_id),
  INDEX idx_avatars_weapon_id (weapon_id)
) COMMENT='我的角色基础与完整快照';

CREATE TABLE IF NOT EXISTS hutao_avatar_relics (
  uid VARCHAR(32) NOT NULL COMMENT '游戏 UID',
  avatar_id BIGINT UNSIGNED NOT NULL COMMENT '角色 ID',
  equip_pos INT NOT NULL COMMENT '圣遗物部位',
  reliquary_id BIGINT UNSIGNED NULL COMMENT '圣遗物 ID',
  name VARCHAR(128) NULL COMMENT '圣遗物名称',
  rarity INT NULL COMMENT '圣遗物星级',
  level INT NULL COMMENT '圣遗物等级',
  set_id BIGINT UNSIGNED NULL COMMENT '圣遗物套装 ID',
  set_name VARCHAR(128) NULL COMMENT '圣遗物套装名称',
  main_property_type INT NULL COMMENT '主词条属性类型',
  main_property_value VARCHAR(64) NULL COMMENT '主词条数值',
  sub_properties_json JSON NULL COMMENT '副词条列表 JSON',
  raw_json JSON NOT NULL COMMENT '圣遗物完整原始 JSON',
  PRIMARY KEY (uid, avatar_id, equip_pos),
  INDEX idx_avatar_relics_reliquary_id (reliquary_id),
  INDEX idx_avatar_relics_set_id (set_id)
) COMMENT='角色圣遗物';

CREATE TABLE IF NOT EXISTS hutao_avatar_scores (
  uid VARCHAR(32) NOT NULL COMMENT '游戏 UID',
  avatar_id BIGINT UNSIGNED NOT NULL COMMENT '角色 ID',
  total_score DECIMAL(10,4) NOT NULL COMMENT '角色圣遗物总评分，使用胡桃我的角色页同款算法',
  score_algorithm VARCHAR(64) NOT NULL COMMENT '评分算法版本',
  recommended_sub_properties_json JSON NULL COMMENT '米游社推荐副词条属性列表',
  synced_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP COMMENT '同步时间',
  PRIMARY KEY (uid, avatar_id),
  INDEX idx_avatar_scores_total_score (total_score)
) COMMENT='角色圣遗物评分汇总';

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
) COMMENT='角色圣遗物评分明细';

CREATE TABLE IF NOT EXISTS hutao_avatar_skills (
  uid VARCHAR(32) NOT NULL COMMENT '游戏 UID',
  avatar_id BIGINT UNSIGNED NOT NULL COMMENT '角色 ID',
  skill_id BIGINT UNSIGNED NOT NULL COMMENT '技能 ID',
  skill_type INT NULL COMMENT '技能类型',
  level INT NULL COMMENT '技能等级',
  raw_json JSON NOT NULL COMMENT '技能完整原始 JSON',
  PRIMARY KEY (uid, avatar_id, skill_id),
  INDEX idx_avatar_skills_skill_id (skill_id)
) COMMENT='角色技能';

CREATE TABLE IF NOT EXISTS hutao_avatar_constellations (
  uid VARCHAR(32) NOT NULL COMMENT '游戏 UID',
  avatar_id BIGINT UNSIGNED NOT NULL COMMENT '角色 ID',
  position INT NOT NULL COMMENT '命座位置，通常 1-6',
  skill_id BIGINT UNSIGNED NULL COMMENT '命座技能 ID',
  name VARCHAR(128) NULL COMMENT '命座名称',
  is_actived BOOLEAN NOT NULL COMMENT '是否已激活',
  raw_json JSON NOT NULL COMMENT '命座完整原始 JSON',
  PRIMARY KEY (uid, avatar_id, position),
  INDEX idx_avatar_constellations_skill_id (skill_id)
) COMMENT='角色命座';

CREATE TABLE IF NOT EXISTS hutao_backpack_archives (
  local_archive_id CHAR(36) PRIMARY KEY COMMENT '胡桃本地背包档案 ID',
  uid VARCHAR(32) NOT NULL COMMENT '同步时当前选中的游戏 UID',
  name VARCHAR(128) NOT NULL COMMENT '背包档案名称',
  is_selected BOOLEAN NOT NULL COMMENT '是否为本地当前选中档案',
  synced_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP COMMENT '同步时间',
  INDEX idx_backpack_archives_uid (uid)
) COMMENT='背包档案';

CREATE TABLE IF NOT EXISTS hutao_backpack_items (
  id BIGINT AUTO_INCREMENT PRIMARY KEY COMMENT '自增主键',
  uid VARCHAR(32) NOT NULL COMMENT '游戏 UID',
  local_archive_id CHAR(36) NOT NULL COMMENT '胡桃本地背包档案 ID',
  item_id BIGINT UNSIGNED NOT NULL COMMENT '物品 ID',
  item_guid BIGINT UNSIGNED NOT NULL COMMENT '游戏内物品 GUID，材料可能为 0',
  count BIGINT UNSIGNED NOT NULL COMMENT '物品数量',
  level INT NOT NULL COMMENT '武器或圣遗物等级',
  promote_level INT NOT NULL COMMENT '突破等级',
  refinement_rank INT NOT NULL COMMENT '武器精炼等级',
  main_prop_id BIGINT UNSIGNED NULL COMMENT '圣遗物主词条 ID',
  append_prop_ids_json JSON NULL COMMENT '圣遗物副词条 ID 列表 JSON',
  is_locked BOOLEAN NOT NULL COMMENT '是否锁定',
  is_marked BOOLEAN NOT NULL COMMENT '是否标记',
  synced_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP COMMENT '同步时间',
  UNIQUE KEY uk_backpack_archive_guid_item (local_archive_id, item_guid, item_id),
  INDEX idx_backpack_uid_item (uid, item_id),
  INDEX idx_backpack_archive (local_archive_id)
) COMMENT='背包物品';

CREATE TABLE IF NOT EXISTS hutao_gacha_items (
  uid VARCHAR(32) NOT NULL COMMENT '游戏 UID',
  query_type INT NOT NULL COMMENT '查询卡池类型',
  gacha_id BIGINT NOT NULL COMMENT '祈愿记录唯一 ID',
  gacha_type INT NOT NULL COMMENT '实际卡池类型',
  item_id BIGINT UNSIGNED NOT NULL COMMENT '物品 ID',
  item_name VARCHAR(128) NULL COMMENT '物品名称，原始接口存在时写入',
  item_type VARCHAR(64) NULL COMMENT '物品类型，例如角色/武器',
  rank_type INT NULL COMMENT '星级',
  time DATETIME NOT NULL COMMENT '祈愿时间',
  raw_json JSON NULL COMMENT '接口原始祈愿记录 JSON',
  PRIMARY KEY (uid, query_type, gacha_id),
  INDEX idx_gacha_uid_time (uid, time),
  INDEX idx_gacha_item_id (item_id)
) COMMENT='普通祈愿记录';

CREATE TABLE IF NOT EXISTS hutao_beyond_gacha_items (
  uid VARCHAR(32) NOT NULL COMMENT '游戏 UID',
  query_type INT NOT NULL COMMENT '查询卡池类型',
  gacha_id BIGINT NOT NULL COMMENT '祈愿记录唯一 ID',
  gacha_type INT NOT NULL COMMENT '实际卡池类型',
  schedule_id BIGINT NOT NULL COMMENT '卡池排期 ID',
  item_id BIGINT UNSIGNED NOT NULL COMMENT '物品 ID',
  item_name VARCHAR(128) NULL COMMENT '物品名称',
  item_type VARCHAR(64) NULL COMMENT '物品类型',
  rank_type INT NULL COMMENT '星级',
  is_up INT NOT NULL COMMENT '是否 UP，沿用接口数值',
  time DATETIME NOT NULL COMMENT '祈愿时间',
  raw_json JSON NULL COMMENT '接口原始 Beyond 祈愿记录 JSON',
  PRIMARY KEY (uid, query_type, gacha_id),
  INDEX idx_beyond_gacha_uid_time (uid, time),
  INDEX idx_beyond_gacha_item_id (item_id)
) COMMENT='新祈愿/Beyond 祈愿记录';

CREATE TABLE IF NOT EXISTS hutao_daily_notes (
  uid VARCHAR(32) PRIMARY KEY COMMENT '游戏 UID',
  refresh_time DATETIME NOT NULL COMMENT '实时便笺刷新时间',
  current_resin INT NULL COMMENT '当前树脂',
  max_resin INT NULL COMMENT '树脂上限',
  resin_recovery_time INT NULL COMMENT '树脂完全恢复剩余秒数',
  current_home_coin INT NULL COMMENT '当前洞天宝钱',
  max_home_coin INT NULL COMMENT '洞天宝钱上限',
  home_coin_recovery_time INT NULL COMMENT '洞天宝钱恢复剩余秒数',
  finished_task_num INT NULL COMMENT '已完成每日委托数量',
  total_task_num INT NULL COMMENT '每日委托总数',
  current_expedition_num INT NULL COMMENT '当前派遣数量',
  max_expedition_num INT NULL COMMENT '最大派遣数量',
  transformer_json JSON NULL COMMENT '参量质变仪 JSON',
  daily_task_json JSON NULL COMMENT '每日任务/历练点 JSON',
  archon_quest_json JSON NULL COMMENT '魔神任务进度 JSON',
  notify_config_json JSON NULL COMMENT '胡桃本地通知配置 JSON',
  raw_json JSON NOT NULL COMMENT 'DailyNote 完整原始 JSON',
  synced_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP COMMENT '同步时间'
) COMMENT='实时便笺快照';

CREATE TABLE IF NOT EXISTS hutao_daily_note_expeditions (
  uid VARCHAR(32) NOT NULL COMMENT '游戏 UID',
  slot_index INT NOT NULL COMMENT '派遣槽位序号',
  avatar_side_icon VARCHAR(512) NULL COMMENT '派遣角色侧边头像 URL',
  status VARCHAR(32) NULL COMMENT '派遣状态',
  remained_time INT NULL COMMENT '剩余秒数',
  raw_json JSON NOT NULL COMMENT '派遣完整原始 JSON',
  PRIMARY KEY (uid, slot_index)
) COMMENT='实时便笺探索派遣';

CREATE TABLE IF NOT EXISTS hutao_meta_versions (
  source VARCHAR(64) NOT NULL COMMENT '元数据来源，例如 SnapHutaoMetadata',
  lang VARCHAR(16) NOT NULL COMMENT '语言，例如 zh-cn',
  version VARCHAR(64) NULL COMMENT '元数据版本号或更新时间标识',
  synced_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP COMMENT '同步时间',
  PRIMARY KEY (source, lang)
) COMMENT='元数据版本记录';

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
) COMMENT='元数据逐表同步状态';

CREATE TABLE IF NOT EXISTS hutao_meta_regions (
  region VARCHAR(32) NOT NULL COMMENT '服务器区域编码，例如 cn_gf01',
  lang VARCHAR(16) NOT NULL COMMENT '语言',
  name VARCHAR(64) NOT NULL COMMENT '服务器名称，例如 天空岛',
  is_oversea BOOLEAN NOT NULL DEFAULT FALSE COMMENT '是否海外服',
  raw_json JSON NULL COMMENT '完整原始元数据 JSON',
  PRIMARY KEY (region, lang)
) COMMENT='服务器区域字典';

CREATE TABLE IF NOT EXISTS hutao_meta_elements (
  element VARCHAR(32) NOT NULL COMMENT '元素编码，例如 Fire/Water/Ice',
  lang VARCHAR(16) NOT NULL COMMENT '语言',
  name VARCHAR(64) NOT NULL COMMENT '元素名称',
  icon VARCHAR(512) NULL COMMENT '元素图标',
  raw_json JSON NULL COMMENT '完整原始元数据 JSON',
  PRIMARY KEY (element, lang)
) COMMENT='元素字典';

CREATE TABLE IF NOT EXISTS hutao_meta_weapon_types (
  weapon_type INT NOT NULL COMMENT '武器类型枚举值',
  lang VARCHAR(16) NOT NULL COMMENT '语言',
  name VARCHAR(64) NOT NULL COMMENT '武器类型名称',
  raw_json JSON NULL COMMENT '完整原始元数据 JSON',
  PRIMARY KEY (weapon_type, lang)
) COMMENT='武器类型字典';

CREATE TABLE IF NOT EXISTS hutao_meta_equip_types (
  equip_type INT NOT NULL COMMENT '装备部位枚举值',
  lang VARCHAR(16) NOT NULL COMMENT '语言',
  name VARCHAR(64) NOT NULL COMMENT '部位名称，例如 生之花/死之羽',
  raw_json JSON NULL COMMENT '完整原始元数据 JSON',
  PRIMARY KEY (equip_type, lang)
) COMMENT='圣遗物部位字典';

CREATE TABLE IF NOT EXISTS hutao_meta_fight_properties (
  property_type INT NOT NULL COMMENT '战斗属性枚举值',
  lang VARCHAR(16) NOT NULL COMMENT '语言',
  name VARCHAR(64) NOT NULL COMMENT '属性名称，例如 暴击率/攻击力',
  display_name VARCHAR(64) NULL COMMENT '展示名称',
  raw_json JSON NULL COMMENT '完整原始元数据 JSON',
  PRIMARY KEY (property_type, lang)
) COMMENT='战斗属性字典';

CREATE TABLE IF NOT EXISTS hutao_meta_gacha_types (
  gacha_type INT NOT NULL COMMENT '卡池类型枚举值，例如 301',
  lang VARCHAR(16) NOT NULL COMMENT '语言',
  name VARCHAR(128) NOT NULL COMMENT '卡池类型名称',
  raw_json JSON NULL COMMENT '完整原始元数据 JSON',
  PRIMARY KEY (gacha_type, lang)
) COMMENT='祈愿卡池类型字典';

CREATE TABLE IF NOT EXISTS hutao_meta_items (
  item_id BIGINT UNSIGNED NOT NULL COMMENT '物品 ID',
  lang VARCHAR(16) NOT NULL COMMENT '语言',
  name VARCHAR(128) NOT NULL COMMENT '物品名称',
  item_type VARCHAR(64) NULL COMMENT '物品类型，例如 Material/Avatar/Weapon/Reliquary',
  rank_level INT NULL COMMENT '星级或品质',
  icon VARCHAR(512) NULL COMMENT '图标',
  description TEXT NULL COMMENT '描述',
  raw_json JSON NULL COMMENT '完整原始元数据 JSON',
  PRIMARY KEY (item_id, lang),
  INDEX idx_meta_items_name (name)
) COMMENT='统一物品字典，用于背包和祈愿 item_id 兜底关联';

CREATE TABLE IF NOT EXISTS hutao_meta_avatars (
  avatar_id BIGINT UNSIGNED NOT NULL COMMENT '角色 ID',
  lang VARCHAR(16) NOT NULL COMMENT '语言',
  name VARCHAR(128) NOT NULL COMMENT '角色名称',
  element VARCHAR(32) NULL COMMENT '元素编码',
  weapon_type INT NULL COMMENT '武器类型',
  rarity INT NULL COMMENT '星级',
  icon VARCHAR(512) NULL COMMENT '头像图标',
  side_icon VARCHAR(512) NULL COMMENT '侧边头像图标',
  card_image VARCHAR(512) NULL COMMENT '角色卡图',
  raw_json JSON NULL COMMENT '完整原始元数据 JSON',
  PRIMARY KEY (avatar_id, lang),
  INDEX idx_meta_avatars_name (name)
) COMMENT='角色字典';

CREATE TABLE IF NOT EXISTS hutao_meta_avatar_skills (
  avatar_id BIGINT UNSIGNED NOT NULL COMMENT '角色 ID',
  skill_id BIGINT UNSIGNED NOT NULL COMMENT '技能 ID',
  lang VARCHAR(16) NOT NULL COMMENT '语言',
  name VARCHAR(128) NULL COMMENT '技能名称',
  skill_type INT NULL COMMENT '技能类型',
  icon VARCHAR(512) NULL COMMENT '技能图标',
  description TEXT NULL COMMENT '技能描述',
  raw_json JSON NULL COMMENT '完整原始元数据 JSON',
  PRIMARY KEY (avatar_id, skill_id, lang)
) COMMENT='角色技能字典';

CREATE TABLE IF NOT EXISTS hutao_meta_avatar_constellations (
  avatar_id BIGINT UNSIGNED NOT NULL COMMENT '角色 ID',
  constellation_id BIGINT UNSIGNED NOT NULL COMMENT '命座 ID',
  lang VARCHAR(16) NOT NULL COMMENT '语言',
  position INT NOT NULL COMMENT '命座位置，1-6',
  name VARCHAR(128) NULL COMMENT '命座名称',
  icon VARCHAR(512) NULL COMMENT '命座图标',
  effect TEXT NULL COMMENT '命座效果',
  raw_json JSON NULL COMMENT '完整原始元数据 JSON',
  PRIMARY KEY (avatar_id, constellation_id, lang),
  UNIQUE KEY uk_avatar_constellation_pos (avatar_id, position, lang)
) COMMENT='角色命座字典';

CREATE TABLE IF NOT EXISTS hutao_meta_weapons (
  weapon_id BIGINT UNSIGNED NOT NULL COMMENT '武器 ID',
  lang VARCHAR(16) NOT NULL COMMENT '语言',
  name VARCHAR(128) NOT NULL COMMENT '武器名称',
  weapon_type INT NULL COMMENT '武器类型',
  rarity INT NULL COMMENT '星级',
  icon VARCHAR(512) NULL COMMENT '图标',
  description TEXT NULL COMMENT '描述',
  raw_json JSON NULL COMMENT '完整原始元数据 JSON',
  PRIMARY KEY (weapon_id, lang),
  INDEX idx_meta_weapons_name (name)
) COMMENT='武器字典';

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
) COMMENT='角色资料页完整公开资料';

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
) COMMENT='武器资料页完整公开资料';

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
) COMMENT='怪物资料页完整公开资料';

CREATE TABLE IF NOT EXISTS hutao_wiki_monster_curves (
  level INT UNSIGNED NOT NULL COMMENT '等级',
  lang VARCHAR(16) NOT NULL COMMENT '语言',
  curves_json JSON NOT NULL COMMENT '怪物成长曲线 JSON',
  raw_json JSON NULL COMMENT '完整原始元数据 JSON',
  PRIMARY KEY (level, lang)
) COMMENT='怪物成长曲线表';

CREATE TABLE IF NOT EXISTS hutao_meta_reliquary_sets (
  set_id BIGINT UNSIGNED NOT NULL COMMENT '圣遗物套装 ID',
  lang VARCHAR(16) NOT NULL COMMENT '语言',
  name VARCHAR(128) NOT NULL COMMENT '套装名称',
  affixes_json JSON NULL COMMENT '套装效果 JSON',
  raw_json JSON NULL COMMENT '完整原始元数据 JSON',
  PRIMARY KEY (set_id, lang),
  INDEX idx_meta_reliquary_sets_name (name)
) COMMENT='圣遗物套装字典';

CREATE TABLE IF NOT EXISTS hutao_meta_reliquaries (
  reliquary_id BIGINT UNSIGNED NOT NULL COMMENT '圣遗物 ID',
  lang VARCHAR(16) NOT NULL COMMENT '语言',
  name VARCHAR(128) NOT NULL COMMENT '圣遗物名称',
  set_id BIGINT UNSIGNED NULL COMMENT '所属套装 ID',
  equip_type INT NULL COMMENT '圣遗物部位',
  rarity INT NULL COMMENT '星级',
  icon VARCHAR(512) NULL COMMENT '图标',
  description TEXT NULL COMMENT '描述',
  raw_json JSON NULL COMMENT '完整原始元数据 JSON',
  PRIMARY KEY (reliquary_id, lang),
  INDEX idx_meta_reliquaries_set (set_id),
  INDEX idx_meta_reliquaries_name (name)
) COMMENT='圣遗物字典';

CREATE TABLE IF NOT EXISTS hutao_meta_materials (
  material_id BIGINT UNSIGNED NOT NULL COMMENT '材料 ID',
  lang VARCHAR(16) NOT NULL COMMENT '语言',
  name VARCHAR(128) NOT NULL COMMENT '材料名称',
  material_type VARCHAR(64) NULL COMMENT '材料类型',
  rank_level INT NULL COMMENT '品质或星级',
  icon VARCHAR(512) NULL COMMENT '图标',
  description TEXT NULL COMMENT '描述',
  raw_json JSON NULL COMMENT '完整原始元数据 JSON',
  PRIMARY KEY (material_id, lang),
  INDEX idx_meta_materials_name (name)
) COMMENT='材料字典';

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
) COMMENT='展示物品字典';

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
) COMMENT='Beyond 物品字典';

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
) COMMENT='祈愿卡池事件字典';

CREATE TABLE IF NOT EXISTS hutao_meta_reliquary_main_affixes (
  affix_id BIGINT UNSIGNED NOT NULL COMMENT '圣遗物主词条 ID',
  lang VARCHAR(16) NOT NULL COMMENT '语言',
  property_type INT NOT NULL COMMENT '战斗属性枚举值',
  raw_json JSON NULL COMMENT '完整原始元数据 JSON',
  PRIMARY KEY (affix_id, lang)
) COMMENT='圣遗物主词条字典';

CREATE TABLE IF NOT EXISTS hutao_meta_reliquary_main_affix_levels (
  rank_level INT NOT NULL COMMENT '圣遗物星级',
  level INT UNSIGNED NOT NULL COMMENT '圣遗物等级',
  lang VARCHAR(16) NOT NULL COMMENT '语言',
  properties_json JSON NOT NULL COMMENT '主词条属性值 JSON',
  raw_json JSON NULL COMMENT '完整原始元数据 JSON',
  PRIMARY KEY (rank_level, level, lang)
) COMMENT='圣遗物主词条等级数值表';

CREATE TABLE IF NOT EXISTS hutao_meta_reliquary_sub_affixes (
  affix_id BIGINT UNSIGNED NOT NULL COMMENT '圣遗物副词条 ID',
  lang VARCHAR(16) NOT NULL COMMENT '语言',
  property_type INT NOT NULL COMMENT '战斗属性枚举值',
  affix_value DOUBLE NOT NULL COMMENT '副词条单次强化值',
  raw_json JSON NULL COMMENT '完整原始元数据 JSON',
  PRIMARY KEY (affix_id, lang)
) COMMENT='圣遗物副词条字典';

CREATE TABLE IF NOT EXISTS hutao_meta_avatar_curves (
  level INT UNSIGNED NOT NULL COMMENT '等级',
  lang VARCHAR(16) NOT NULL COMMENT '语言',
  curves_json JSON NOT NULL COMMENT '角色成长曲线 JSON',
  raw_json JSON NULL COMMENT '完整原始元数据 JSON',
  PRIMARY KEY (level, lang)
) COMMENT='角色成长曲线表';

CREATE TABLE IF NOT EXISTS hutao_meta_weapon_curves (
  level INT UNSIGNED NOT NULL COMMENT '等级',
  lang VARCHAR(16) NOT NULL COMMENT '语言',
  curves_json JSON NOT NULL COMMENT '武器成长曲线 JSON',
  raw_json JSON NULL COMMENT '完整原始元数据 JSON',
  PRIMARY KEY (level, lang)
) COMMENT='武器成长曲线表';

CREATE TABLE IF NOT EXISTS hutao_meta_avatar_promotes (
  promote_id BIGINT UNSIGNED NOT NULL COMMENT '角色突破组 ID',
  promote_level INT UNSIGNED NOT NULL COMMENT '突破等级',
  lang VARCHAR(16) NOT NULL COMMENT '语言',
  add_properties_json JSON NOT NULL COMMENT '突破属性加成 JSON',
  raw_json JSON NULL COMMENT '完整原始元数据 JSON',
  PRIMARY KEY (promote_id, promote_level, lang)
) COMMENT='角色突破加成表';

CREATE TABLE IF NOT EXISTS hutao_meta_weapon_promotes (
  promote_id BIGINT UNSIGNED NOT NULL COMMENT '武器突破组 ID',
  promote_level INT UNSIGNED NOT NULL COMMENT '突破等级',
  lang VARCHAR(16) NOT NULL COMMENT '语言',
  add_properties_json JSON NOT NULL COMMENT '突破属性加成 JSON',
  raw_json JSON NULL COMMENT '完整原始元数据 JSON',
  PRIMARY KEY (promote_id, promote_level, lang)
) COMMENT='武器突破加成表';

CREATE TABLE IF NOT EXISTS hutao_meta_images (
  id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT COMMENT '自增主键',
  category VARCHAR(64) NOT NULL COMMENT '图片分类，如 AvatarIcon、ItemIcon、EquipIcon、RelicIcon、Skill、Talent、NameCardPic、DailyNoteAvatarSideIcon',
  resource_name VARCHAR(255) NOT NULL COMMENT '资源文件名，如 UI_AvatarIcon_Ambor.png',
  resource_url VARCHAR(1024) NOT NULL COMMENT '胡桃静态资源原始 URL，可直接给前端兜底展示',
  content_type VARCHAR(128) NULL COMMENT '图片 MIME 类型，如 image/png',
  content_length INT UNSIGNED NULL COMMENT '图片字节数，可为空；不保存图片二进制',
  synced_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP COMMENT '同步时间',
  PRIMARY KEY (id),
  UNIQUE KEY uk_hutao_meta_images_category_resource (category, resource_name),
  KEY idx_hutao_meta_images_category (category)
) COMMENT='胡桃元数据图片引用表';
