using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Oxide.Core;
using Oxide.Core.Plugins;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("BuildingLicense", "whitecristafer", "1.1.0")]
    [Description("Building License - Sponsored by infunv.ru")]
    public class BuildingLicense : RustPlugin
    {
        #region Constants

        private const ulong PluginIcon = 76561198209258869;
        private const string PluginVersion = "1.1.0";
        private const string Prefix = "<size=12><color=#9966cc><b>[EVOLVE]</b></color></size>";
        private const string AdminPermission = "buildinglicense.admin";
        private const string StonePermission = "buildinglicense.stone";
        private const string MetalPermission = "buildinglicense.metal";
        private const string ArmoredPermission = "buildinglicense.armored";

        private const string DefaultUpdateUrl = "https://raw.githubusercontent.com/whitecristafer/BuildingLicense/main/BuildingLicense.cs";

        private static readonly string[] AllCategories = { "foundation", "wall", "floor", "stair", "roof", "ramp", "other" };

        #endregion

        #region Config

        private PluginConfig _config;
        private string _dataPath;
        private string _backupPath;
        private Timer _updateTimer;
        private readonly Dictionary<ulong, string> _cachedNames = new Dictionary<ulong, string>();

        private sealed class PluginConfig
        {
            public SettingsConfig Settings = new SettingsConfig();
            public UpdateConfig Update = new UpdateConfig();
            public MessagesConfig Messages = new MessagesConfig();
            public PermissionsConfig Permissions = new PermissionsConfig();
            public GradeRulesConfig GradeRules = new GradeRulesConfig();
        }

        private sealed class SettingsConfig
        {
            public bool Enabled = true;
            public bool BlockWithoutLicense = true;
            public bool ShowMessages = true;
            public bool LogToConsole = true;
            public bool AllowAdminsBypass = false;
            public bool UseCategoryRestrictions = true;
            public bool AllowMultipleLicenses = true;
        }

        private sealed class UpdateConfig
        {
            public bool Enabled = true;
            public bool CheckOnStartup = true;
            public bool AutoCheck = true;
            public int CheckIntervalMinutes = 360;
            public string SourceUrl = string.Empty;
            public int TimeoutSeconds = 15;
            public bool CreateBackupBeforeApply = true;
        }

        private sealed class MessagesConfig
        {
            public string Prefix = string.Empty;

            public string NoPermission = "У вас нет доступа к этой команде / You do not have permission.";
            public string PluginDisabled = "Плагин отключён в конфиге / Plugin is disabled in config.";
            public string NoLicenseStone = "У вас нет лицензии на Stone / Камень улучшение.";
            public string NoLicenseMetal = "У вас нет лицензии на Metal / Металл улучшение.";
            public string NoLicenseArmored = "У вас нет лицензии на Armored / Броню улучшение.";
            public string GradeBlocked = "Этот уровень улучшения отключён в конфиге / This grade is disabled in config.";
            public string CategoryBlocked = "Этот тип постройки ограничен для данного уровня / This building category is restricted for this grade.";
            public string Granted = "Лицензия выдана: {0} -> {1}";
            public string AlreadyHas = "У игрока уже есть эта лицензия / Player already has this license.";
            public string PlayerNotFound = "Игрок не найден / Player not found.";
            public string InvalidLicense = "Неверный тип лицензии. Используй: stone | metal | armored.";
            public string HelpHeader = "BuildingLicense help / справка";
            public string HelpLine1 = "/grantlicense <player> <stone|metal|armored> - выдать лицензию";
            public string HelpLine2 = "/bl help - показать список команд";
            public string HelpLine3 = "/bl update - проверка и авто-обновление";
            public string HelpLine4 = "/bl reload - перезагрузить конфиг";
            public string HelpLine5 = "/bl status - показать статус плагина";
            public string HelpLine6 = "Лицензии можно совмещать: stone + metal + armored.";
            public string UpdateCheckStart = "Проверяю обновления...";
            public string UpdateCurrent = "Установлена актуальная версия: {0}";
            public string UpdateAvailable = "Найдена новая версия: {0} -> {1}. Загружаю...";
            public string UpdateDownloaded = "Обновление сохранено: {0}";
            public string UpdateFailed = "Не удалось проверить или загрузить обновление.";
            public string UpdateInvalid = "Удалённый файл не содержит валидной версии.";
            public string ConfigReloaded = "Конфиг и кэши обновлены.";
        }

        private sealed class PermissionsConfig
        {
            public string Admin = string.Empty;
            public string Stone = string.Empty;
            public string Metal = string.Empty;
            public string Armored = string.Empty;
        }

        private sealed class GradeRulesConfig
        {
            public GradeRuleConfig Stone = new GradeRuleConfig();
            public GradeRuleConfig Metal = new GradeRuleConfig();
            public GradeRuleConfig Armored = new GradeRuleConfig();
            public List<string> DisabledGrades = new List<string>();
        }

        private sealed class GradeRuleConfig
        {
            public bool Enabled = true;
            public string Permission = string.Empty;
            public string Message = string.Empty;
            public List<string> AllowedCategories = new List<string>();
        }

        #endregion

        #region Runtime caches

        private readonly HashSet<BuildingGrade.Enum> _disabledGrades = new HashSet<BuildingGrade.Enum>();
        private readonly Dictionary<BuildingGrade.Enum, GradeRuntime> _gradeCache = new Dictionary<BuildingGrade.Enum, GradeRuntime>();
        private readonly Dictionary<string, string> _categoryCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private sealed class GradeRuntime
        {
            public BuildingGrade.Enum Grade;
            public string Permission;
            public string Message;
            public bool Enabled;
            public HashSet<string> AllowedCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        #endregion

        #region Hooks

        private void Init()
        {
            EnsureFolders();
            RegisterPermissions();
        }

        private void Loaded()
        {
            LoadPluginConfig();
            RegisterPermissions();
            BuildCaches();
            RegisterChatCommands();
        }

        private void OnServerInitialized()
        {
            PrintStartupBanner();

            if (_config.Update.Enabled && _config.Update.CheckOnStartup)
            {
                timer.Once(3f, () => CheckForUpdates(false));
            }

            StartAutoUpdateLoop();
        }

        private void Unload()
        {
            if (_updateTimer != null)
            {
                _updateTimer.Destroy();
                _updateTimer = null;
            }
        }

        private object CanUpgrade(BasePlayer player, BuildingBlock block, ConstructionGrade grade)
        {
            return HandleUpgradeHook(player, block, GetGradeEnum(grade), true);
        }

        private object CanUpgrade(BuildingBlock block, BasePlayer player, ConstructionGrade grade)
        {
            return HandleUpgradeHook(player, block, GetGradeEnum(grade), true);
        }

        private object CanUpgrade(BasePlayer player, BuildingBlock block, BuildingGrade.Enum grade)
        {
            return HandleUpgradeHook(player, block, grade, true);
        }

        private object CanUpgrade(BuildingBlock block, BasePlayer player, BuildingGrade.Enum grade)
        {
            return HandleUpgradeHook(player, block, grade, true);
        }

        private object OnStructureUpgrade(BuildingBlock block, BasePlayer player, BuildingGrade.Enum grade)
        {
            return HandleUpgradeHook(player, block, grade, false);
        }

        private object OnStructureUpgrade(BuildingBlock block, BasePlayer player, ConstructionGrade grade)
        {
            return HandleUpgradeHook(player, block, GetGradeEnum(grade), false);
        }

        #endregion

        #region Commands

        private void RegisterChatCommands()
        {
            cmd.AddChatCommand("bl", this, nameof(CmdBL));
            cmd.AddChatCommand("grantlicense", this, nameof(CmdGrantLicense));
        }

        private void CmdBL(BasePlayer player, string command, string[] args)
        {
            if (!CanUseAdmin(player))
            {
                SendMessage(player, _config.Messages.NoPermission);
                return;
            }

            string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "help";

            switch (sub)
            {
                case "help":
                    SendMessage(player, _config.Messages.HelpHeader);
                    SendMessage(player, _config.Messages.HelpLine1);
                    SendMessage(player, _config.Messages.HelpLine2);
                    SendMessage(player, _config.Messages.HelpLine3);
                    SendMessage(player, _config.Messages.HelpLine4);
                    SendMessage(player, _config.Messages.HelpLine5);
                    SendMessage(player, _config.Messages.HelpLine6);
                    break;

                case "status":
                    SendMessage(player, string.Format("Enabled: {0} | BlockWithoutLicense: {1} | ShowMessages: {2}",
                        _config.Settings.Enabled, _config.Settings.BlockWithoutLicense, _config.Settings.ShowMessages));
                    SendMessage(player, string.Format("Update: {0} | Source: {1}", _config.Update.Enabled, _config.Update.SourceUrl));
                    break;

                case "reload":
                    LoadPluginConfig();
                    RegisterPermissions();
                    BuildCaches();
                    SendMessage(player, _config.Messages.ConfigReloaded);
                    break;

                case "update":
                    CheckForUpdates(true);
                    break;

                default:
                    SendMessage(player, _config.Messages.HelpHeader);
                    SendMessage(player, _config.Messages.HelpLine1);
                    SendMessage(player, _config.Messages.HelpLine2);
                    SendMessage(player, _config.Messages.HelpLine3);
                    SendMessage(player, _config.Messages.HelpLine4);
                    SendMessage(player, _config.Messages.HelpLine5);
                    SendMessage(player, _config.Messages.HelpLine6);
                    break;
            }
        }

        private void CmdGrantLicense(BasePlayer player, string command, string[] args)
        {
            if (!CanUseAdmin(player))
            {
                SendMessage(player, _config.Messages.NoPermission);
                return;
            }

            if (args.Length < 2)
            {
                SendMessage(player, "Usage: /grantlicense <player> <stone|metal|armored>");
                return;
            }

            BasePlayer target = FindPlayer(args[0]);
            if (target == null)
            {
                SendMessage(player, _config.Messages.PlayerNotFound);
                return;
            }

            LicenseTier tier;
            if (!TryParseTier(args[1], out tier))
            {
                SendMessage(player, _config.Messages.InvalidLicense);
                return;
            }

            string permissionNode = GetPermissionForTier(tier);
            if (permission.UserHasPermission(target.UserIDString, permissionNode))
            {
                SendMessage(player, _config.Messages.AlreadyHas);
                return;
            }

            permission.GrantUserPermission(target.UserIDString, permissionNode, this);

            SendMessage(player, string.Format(_config.Messages.Granted, target.displayName, tier.ToString().ToLowerInvariant()));
            Puts(string.Format("{0} granted {1} license to {2} ({3})",
                GetLogTag(), tier, target.displayName, target.UserIDString));
        }

        #endregion

        #region Upgrade logic

        private object HandleUpgradeHook(BasePlayer player, BuildingBlock block, BuildingGrade.Enum grade, bool fromCanUpgrade)
        {
            if (player == null || block == null)
                return null;

            if (!_config.Settings.Enabled || !_config.Settings.BlockWithoutLicense)
                return null;

            if (player.IsAdmin && _config.Settings.AllowAdminsBypass)
                return null;

            if (grade <= BuildingGrade.Enum.Wood)
                return null;

            GradeRuntime runtime;
            if (!_gradeCache.TryGetValue(grade, out runtime))
                return null;

            if (!runtime.Enabled)
            {
                if (_config.Settings.ShowMessages)
                    SendMessage(player, _config.Messages.GradeBlocked);

                LogBlocked(player, block, grade, "disabled-grade");
                return false;
            }

            if (!IsCategoryAllowed(runtime, block))
            {
                if (_config.Settings.ShowMessages)
                    SendMessage(player, _config.Messages.CategoryBlocked);

                LogBlocked(player, block, grade, "category-restricted");
                return false;
            }

            if (string.IsNullOrWhiteSpace(runtime.Permission))
                return null;

            if (!permission.UserHasPermission(player.UserIDString, runtime.Permission))
            {
                if (_config.Settings.ShowMessages)
                {
                    string msg = runtime.Message;
                    if (string.IsNullOrWhiteSpace(msg))
                        msg = GetDefaultDenyMessage(grade);

                    string blockName = "конструкции";
                    string category = ResolveCategory(block);
                    
                    if (category == "foundation") blockName = "Фундамента";
                    else if (category == "wall") blockName = "Стены";
                    else if (category == "floor") blockName = "Потолка";
                    else if (category == "stair") blockName = "Лестницы";
                    else if (category == "roof") blockName = "Крыши";
                    else if (category == "ramp") blockName = "Рампы";

                    if (msg.Contains("{0}"))
                    {
                        try { msg = string.Format(msg, blockName); } catch { }
                    }

                    SendMessage(player, msg);
                }

                LogBlocked(player, block, grade, "missing-permission");
                return false;
            }

            return null;
        }

        private bool IsCategoryAllowed(GradeRuntime runtime, BuildingBlock block)
        {
            if (!_config.Settings.UseCategoryRestrictions)
                return true;

            if (runtime == null || runtime.AllowedCategories == null || runtime.AllowedCategories.Count == 0)
                return true;

            string category = ResolveCategory(block);
            if (string.IsNullOrEmpty(category))
                category = "other";

            return runtime.AllowedCategories.Contains(category);
        }

        private string ResolveCategory(BuildingBlock block)
        {
            if (block == null)
                return "other";

            string key = null;
            try
            {
                key = block.ShortPrefabName;
            }
            catch
            {
                key = null;
            }

            if (string.IsNullOrWhiteSpace(key))
                key = block.name;

            if (string.IsNullOrWhiteSpace(key))
                return "other";

            string cached;
            if (_categoryCache.TryGetValue(key, out cached))
                return cached;

            string lower = key.ToLowerInvariant();
            string category = "other";

            if (lower.Contains("foundation"))
                category = "foundation";
            else if (lower.Contains("wall"))
                category = "wall";
            else if (lower.Contains("floor"))
                category = "floor";
            else if (lower.Contains("stair"))
                category = "stair";
            else if (lower.Contains("roof"))
                category = "roof";
            else if (lower.Contains("ramp"))
                category = "ramp";

            _categoryCache[key] = category;
            return category;
        }

        #endregion

        #region Update system

        private void StartAutoUpdateLoop()
        {
            if (_updateTimer != null)
            {
                _updateTimer.Destroy();
                _updateTimer = null;
            }

            if (!_config.Update.Enabled || !_config.Update.AutoCheck)
                return;

            float interval = Mathf.Max(15f, _config.Update.CheckIntervalMinutes * 60f);
            _updateTimer = timer.Every(interval, () => CheckForUpdates(false));
        }

        private void CheckForUpdates(bool manual)
        {
            if (!_config.Update.Enabled)
                return;

            string url = (_config.Update.SourceUrl ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(url))
            {
                if (manual)
                    SendMessageToConsole(_config.Messages.UpdateFailed);
                PrintWarning("[BuildingLicense] Update source URL is empty.");
                return;
            }

            if (manual)
                SendMessageToConsole(_config.Messages.UpdateCheckStart);

            Dictionary<string, string> headers = new Dictionary<string, string>
            {
                ["User-Agent"] = string.Format("{0}/{1}", Name, PluginVersion),
                ["Accept"] = "text/plain, */*"
            };

            webrequest.Enqueue(url, null, (code, response) =>
            {
                if (code != 200 || string.IsNullOrWhiteSpace(response))
                {
                    if (manual)
                        SendMessageToConsole(_config.Messages.UpdateFailed);

                    PrintWarning(string.Format("[BuildingLicense] Update check failed. HTTP {0}", code));
                    return;
                }

                string remoteVersionRaw = ExtractVersion(response);
                if (string.IsNullOrWhiteSpace(remoteVersionRaw))
                {
                    if (manual)
                        SendMessageToConsole(_config.Messages.UpdateInvalid);

                    PrintWarning("[BuildingLicense] Remote source does not contain a valid version.");
                    return;
                }

                Version remoteVersion;
                Version localVersion;
                if (!TryParseVersion(remoteVersionRaw, out remoteVersion) || !TryParseVersion(PluginVersion, out localVersion))
                {
                    if (manual)
                        SendMessageToConsole(_config.Messages.UpdateInvalid);

                    PrintWarning("[BuildingLicense] Version parse failed.");
                    return;
                }

                if (remoteVersion <= localVersion)
                {
                    if (manual)
                        SendMessageToConsole(string.Format(_config.Messages.UpdateCurrent, localVersion));

                    Puts(string.Format("{0} Update skipped. Local version is current: {1}", GetLogTag(), localVersion));
                    return;
                }

                if (manual)
                    SendMessageToConsole(string.Format(_config.Messages.UpdateAvailable, localVersion, remoteVersionRaw));

                TryApplyUpdate(response, remoteVersionRaw);
            }, this, Oxide.Core.Libraries.RequestMethod.GET, headers, _config.Update.TimeoutSeconds);
        }

        private void TryApplyUpdate(string sourceContent, string remoteVersionRaw)
        {
            string currentFile = Path.Combine(Interface.Oxide.PluginDirectory, Name + ".cs");

            if (_config.Update.CreateBackupBeforeApply)
            {
                TryCreateBackup(currentFile);
            }

            try
            {
                File.WriteAllText(currentFile, sourceContent, new UTF8Encoding(false));
                Puts(string.Format("{0} Update downloaded to: {1}", GetLogTag(), currentFile));
                Puts("[BuildingLicense] Reloading plugin after update...");

                timer.Once(3f, () =>
                {
                    try
                    {
                        Server.Command("oxide.reload " + Name);
                    }
                    catch (Exception ex)
                    {
                        PrintError("[BuildingLicense] Reload after update failed: " + ex.Message);
                    }
                });
            }
            catch (Exception ex)
            {
                PrintError("[BuildingLicense] Failed to write update file: " + ex.Message);
            }
        }

        private void TryCreateBackup(string currentFile)
        {
            try
            {
                if (!File.Exists(currentFile))
                    return;

                string fileName = string.Format("{0}_{1}.bak.cs", Name, DateTime.UtcNow.ToString("yyyyMMdd_HHmmss"));
                string backupFile = Path.Combine(_backupPath, fileName);
                File.Copy(currentFile, backupFile, true);
            }
            catch (Exception ex)
            {
                PrintWarning("[BuildingLicense] Failed to create update backup: " + ex.Message);
            }
        }

        private string ExtractVersion(string source)
        {
            if (string.IsNullOrWhiteSpace(source))
                return null;
            Match infoMatch = Regex.Match(source,
                @"\[Info\(\s*""[^""]+""\s*,\s*""[^""]+""\s*,\s*""(?<version>[^""]+)""\s*\)\]",
                RegexOptions.IgnoreCase | RegexOptions.Multiline);

            if (infoMatch.Success)
                return infoMatch.Groups["version"].Value.Trim();
            Match fieldMatch = Regex.Match(source,
                @"Version\s*=\s*""(?<version>[^""]+)""",
                RegexOptions.IgnoreCase | RegexOptions.Multiline);

            if (fieldMatch.Success)
                return fieldMatch.Groups["version"].Value.Trim();
            Match pluginVersionMatch = Regex.Match(source,
                @"PluginVersion\s*=\s*""(?<version>[^""]+)""",
                RegexOptions.IgnoreCase | RegexOptions.Multiline);

            return pluginVersionMatch.Success ? pluginVersionMatch.Groups["version"].Value.Trim() : null;
        }

        private bool TryParseVersion(string value, out Version version)
        {
            version = null;
            if (string.IsNullOrWhiteSpace(value))
                return false;

            string trimmed = value.Trim().TrimStart('v', 'V');
            if (!Regex.IsMatch(trimmed, @"^\d+(\.\d+){1,3}$")) 
                return false;

            try
            {
                version = new Version(trimmed);
                return true;
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region Config

        protected override void LoadDefaultConfig()
        {
            _config = CreateDefaultConfig();
            SaveConfig();
        }

        private void LoadPluginConfig()
        {
            try
            {
                _config = Config.ReadObject<PluginConfig>();
            }
            catch
            {
                PrintWarning("[BuildingLicense] Config is broken. Rebuilding default config.");
                _config = null;
            }

            if (_config == null)
                _config = CreateDefaultConfig();

            NormalizeConfig();
            SaveConfig();
        }

        private PluginConfig CreateDefaultConfig()
        {
            PluginConfig cfg = new PluginConfig();

            cfg.Settings.Enabled = true;
            cfg.Settings.BlockWithoutLicense = true;
            cfg.Settings.ShowMessages = true;
            cfg.Settings.LogToConsole = true;
            cfg.Settings.AllowAdminsBypass = false;
            cfg.Settings.UseCategoryRestrictions = true;
            cfg.Settings.AllowMultipleLicenses = true;

            cfg.Update.Enabled = true;
            cfg.Update.CheckOnStartup = true;
            cfg.Update.AutoCheck = true;
            cfg.Update.CheckIntervalMinutes = 360;
            cfg.Update.SourceUrl = DefaultUpdateUrl;
            cfg.Update.TimeoutSeconds = 15;
            cfg.Update.CreateBackupBeforeApply = true;

            cfg.Messages.Prefix = Prefix;
            cfg.Messages.NoPermission = "У вас нет доступа к этой команде";
            cfg.Messages.PluginDisabled = "Плагин отключён в конфиге";
                cfg.Messages.NoLicenseStone = "<color=#ffaa00>Для улучшения</color> <color=#ffffff>{0}</color> <color=#ffaa00>необходима лицензия</color> <color=#9966cc><b>Stone</b></color>!";
            cfg.Messages.NoLicenseMetal = "<color=#ffaa00>Для улучшения</color> <color=#ffffff>{0}</color> <color=#ffaa00>необходима лицензия</color> <color=#e5e5e5><b>Metal</b></color>!";
            cfg.Messages.NoLicenseArmored = "<color=#ffaa00>Для улучшения</color> <color=#ffffff>{0}</color> <color=#ffaa00>необходима лицензия</color> <color=#42aaff><b>Armored</b></color>!";
            cfg.Messages.GradeBlocked = "Этот уровень улучшения отключён в конфиге";
            cfg.Messages.CategoryBlocked = "Этот тип постройки ограничен для данного уровня";
            cfg.Messages.Granted = "Лицензия выдана: {0} -> {1}";
            cfg.Messages.AlreadyHas = "У игрока уже есть эта лицензия";
            cfg.Messages.PlayerNotFound = "Игрок не найден";
            cfg.Messages.InvalidLicense = "Неверный тип лицензии. Используй: stone | metal | armored.";
            cfg.Messages.HelpHeader = "BuildingLicense help / справка";
            cfg.Messages.HelpLine1 = "/grantlicense <player> <stone|metal|armored> - выдать лицензию";
            cfg.Messages.HelpLine2 = "/bl help - показать список команд";
            cfg.Messages.HelpLine3 = "/bl update - проверка и авто-обновление";
            cfg.Messages.HelpLine4 = "/bl reload - перезагрузить конфиг";
            cfg.Messages.HelpLine5 = "/bl status - показать статус плагина";
            cfg.Messages.HelpLine6 = "Лицензии можно совмещать: stone + metal + armored.";
            cfg.Messages.UpdateCheckStart = "Проверяю обновления...";
            cfg.Messages.UpdateCurrent = "Установлена актуальная версия: {0}";
            cfg.Messages.UpdateAvailable = "Найдена новая версия: {0} -> {1}. Загружаю...";
            cfg.Messages.UpdateDownloaded = "Обновление сохранено: {0}";
            cfg.Messages.UpdateFailed = "Не удалось проверить или загрузить обновление.";
            cfg.Messages.UpdateInvalid = "Удалённый файл не содержит валидной версии.";
            cfg.Messages.ConfigReloaded = "Конфиг и кэши обновлены.";

            cfg.Permissions.Admin = AdminPermission;
            cfg.Permissions.Stone = StonePermission;
            cfg.Permissions.Metal = MetalPermission;
            cfg.Permissions.Armored = ArmoredPermission;

            cfg.GradeRules.DisabledGrades = new List<string>();

            cfg.GradeRules.Stone.Enabled = true;
            cfg.GradeRules.Stone.Permission = StonePermission;
            cfg.GradeRules.Stone.Message = cfg.Messages.NoLicenseStone;
            cfg.GradeRules.Stone.AllowedCategories = new List<string>(AllCategories);

            cfg.GradeRules.Metal.Enabled = true;
            cfg.GradeRules.Metal.Permission = MetalPermission;
            cfg.GradeRules.Metal.Message = cfg.Messages.NoLicenseMetal;
            cfg.GradeRules.Metal.AllowedCategories = new List<string>(AllCategories);

            cfg.GradeRules.Armored.Enabled = true;
            cfg.GradeRules.Armored.Permission = ArmoredPermission;
            cfg.GradeRules.Armored.Message = cfg.Messages.NoLicenseArmored;
            cfg.GradeRules.Armored.AllowedCategories = new List<string>(AllCategories);

            return cfg;
        }

        private void NormalizeConfig()
        {
            if (_config.Settings == null) _config.Settings = new SettingsConfig();
            if (_config.Update == null) _config.Update = new UpdateConfig();
            if (_config.Messages == null) _config.Messages = new MessagesConfig();
            if (_config.Permissions == null) _config.Permissions = new PermissionsConfig();
            if (_config.GradeRules == null) _config.GradeRules = new GradeRulesConfig();

            if (string.IsNullOrWhiteSpace(_config.Update.SourceUrl))
                _config.Update.SourceUrl = DefaultUpdateUrl;

            if (_config.Update.TimeoutSeconds <= 0)
                _config.Update.TimeoutSeconds = 15;

            if (_config.Update.CheckIntervalMinutes <= 0)
                _config.Update.CheckIntervalMinutes = 360;

            if (_config.Messages.Prefix != Prefix)
                _config.Messages.Prefix = Prefix;

            if (_config.Permissions.Admin != AdminPermission)
                _config.Permissions.Admin = AdminPermission;
            if (_config.Permissions.Stone != StonePermission)
                _config.Permissions.Stone = StonePermission;
            if (_config.Permissions.Metal != MetalPermission)
                _config.Permissions.Metal = MetalPermission;
            if (_config.Permissions.Armored != ArmoredPermission)
                _config.Permissions.Armored = ArmoredPermission;

            if (_config.GradeRules.DisabledGrades == null)
                _config.GradeRules.DisabledGrades = new List<string>();

            EnsureRuleDefaults(_config.GradeRules.Stone, StonePermission, _config.Messages.NoLicenseStone);
            EnsureRuleDefaults(_config.GradeRules.Metal, MetalPermission, _config.Messages.NoLicenseMetal);
            EnsureRuleDefaults(_config.GradeRules.Armored, ArmoredPermission, _config.Messages.NoLicenseArmored);
        }

        private void EnsureRuleDefaults(GradeRuleConfig rule, string permissionNode, string message)
        {
            if (rule == null)
                return;

            if (string.IsNullOrWhiteSpace(rule.Permission))
                rule.Permission = permissionNode;

            if (string.IsNullOrWhiteSpace(rule.Message))
                rule.Message = message;

            if (rule.AllowedCategories == null || rule.AllowedCategories.Count == 0)
                rule.AllowedCategories = new List<string>(AllCategories);
        }

        private void SaveConfig()
        {
            Config.WriteObject(_config, true);
        }

        #endregion

        #region Cache and permissions

        private void RegisterPermissions()
        {
            permission.RegisterPermission(AdminPermission, this);
            permission.RegisterPermission(StonePermission, this);
            permission.RegisterPermission(MetalPermission, this);
            permission.RegisterPermission(ArmoredPermission, this);
        }

        private void BuildCaches()
        {
            _disabledGrades.Clear();
            _gradeCache.Clear();
            _categoryCache.Clear();

            if (_config.GradeRules.DisabledGrades != null)
            {
                foreach (string name in _config.GradeRules.DisabledGrades)
                {
                    BuildingGrade.Enum grade;
                    if (TryParseGrade(name, out grade))
                        _disabledGrades.Add(grade);
                }
            }

            AddGradeRuntime(BuildingGrade.Enum.Stone, _config.GradeRules.Stone);
            AddGradeRuntime(BuildingGrade.Enum.Metal, _config.GradeRules.Metal);
            AddGradeRuntime(BuildingGrade.Enum.TopTier, _config.GradeRules.Armored);
        }

        private void AddGradeRuntime(BuildingGrade.Enum grade, GradeRuleConfig rule)
        {
            GradeRuntime runtime = new GradeRuntime();
            runtime.Grade = grade;
            runtime.Enabled = rule != null && rule.Enabled;
            runtime.Permission = rule != null ? rule.Permission : string.Empty;
            runtime.Message = rule != null ? rule.Message : string.Empty;
            runtime.AllowedCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (rule != null && rule.AllowedCategories != null)
            {
                foreach (string category in rule.AllowedCategories)
                {
                    if (string.IsNullOrWhiteSpace(category))
                        continue;

                    runtime.AllowedCategories.Add(category.Trim().ToLowerInvariant());
                }
            }

            _gradeCache[grade] = runtime;
        }

        #endregion

        #region Helpers

        private void EnsureFolders()
        {
            _dataPath = Path.Combine(Interface.Oxide.DataDirectory, Name);
            _backupPath = Path.Combine(_dataPath, "backups");

            Directory.CreateDirectory(_dataPath);
            Directory.CreateDirectory(_backupPath);
        }

        private bool CanUseAdmin(BasePlayer player)
        {
            if (player == null)
                return false;

            if (player.IsAdmin && _config.Settings.AllowAdminsBypass)
                return true;

            return permission.UserHasPermission(player.UserIDString, AdminPermission);
        }

        private BasePlayer FindPlayer(string nameOrId)
        {
            if (string.IsNullOrWhiteSpace(nameOrId))
                return null;

            BasePlayer player = BasePlayer.Find(nameOrId);
            if (player != null)
                return player;

            player = BasePlayer.FindSleeping(nameOrId);
            if (player != null)
                return player;

            string needle = nameOrId.Trim();
            foreach (BasePlayer active in BasePlayer.activePlayerList)
            {
                if (active == null)
                    continue;

                if (MatchesPlayer(active, needle))
                    return active;
            }

            foreach (BasePlayer sleeping in BasePlayer.sleepingPlayerList)
            {
                if (sleeping == null)
                    continue;

                if (MatchesPlayer(sleeping, needle))
                    return sleeping;
            }

            return null;
        }

        private bool MatchesPlayer(BasePlayer player, string needle)
        {
            if (player == null || string.IsNullOrWhiteSpace(needle))
                return false;

            if (player.UserIDString == needle)
                return true;

            return player.displayName != null &&
                   player.displayName.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private LicenseTier GetTierFromGrade(BuildingGrade.Enum grade)
        {
            switch (grade)
            {
                case BuildingGrade.Enum.Stone:
                    return LicenseTier.Stone;
                case BuildingGrade.Enum.Metal:
                    return LicenseTier.Metal;
                case BuildingGrade.Enum.TopTier:
                    return LicenseTier.Armored;
                default:
                    return LicenseTier.Stone;
            }
        }

        private bool TryParseTier(string value, out LicenseTier tier)
        {
            tier = LicenseTier.Stone;

            if (string.IsNullOrWhiteSpace(value))
                return false;

            switch (value.Trim().ToLowerInvariant())
            {
                case "stone":
                    tier = LicenseTier.Stone;
                    return true;
                case "metal":
                    tier = LicenseTier.Metal;
                    return true;
                case "armored":
                case "top":
                case "toptier":
                case "armor":
                    tier = LicenseTier.Armored;
                    return true;
                default:
                    return false;
            }
        }

        private string GetPermissionForTier(LicenseTier tier)
        {
            switch (tier)
            {
                case LicenseTier.Stone:
                    return _config.Permissions.Stone;
                case LicenseTier.Metal:
                    return _config.Permissions.Metal;
                case LicenseTier.Armored:
                    return _config.Permissions.Armored;
                default:
                    return _config.Permissions.Stone;
            }
        }

        private string GetDefaultDenyMessage(BuildingGrade.Enum grade)
        {
            switch (grade)
            {
                case BuildingGrade.Enum.Stone:
                    return _config.Messages.NoLicenseStone;
                case BuildingGrade.Enum.Metal:
                    return _config.Messages.NoLicenseMetal;
                case BuildingGrade.Enum.TopTier:
                    return _config.Messages.NoLicenseArmored;
                default:
                    return _config.Messages.NoLicenseStone;
            }
        }

        private bool TryParseGrade(string value, out BuildingGrade.Enum grade)
        {
            grade = BuildingGrade.Enum.None;

            if (string.IsNullOrWhiteSpace(value))
                return false;

            switch (value.Trim().ToLowerInvariant())
            {
                case "stone":
                    grade = BuildingGrade.Enum.Stone;
                    return true;
                case "metal":
                    grade = BuildingGrade.Enum.Metal;
                    return true;
                case "armored":
                case "top":
                case "toptier":
                case "armor":
                    grade = BuildingGrade.Enum.TopTier;
                    return true;
                case "wood":
                    grade = BuildingGrade.Enum.Wood;
                    return true;
                case "twig":
                case "twigs":
                    grade = BuildingGrade.Enum.Twigs;
                    return true;
                default:
                    return false;
            }
        }

        private BuildingGrade.Enum GetGradeEnum(ConstructionGrade grade)
        {
            if (grade == null || grade.gradeBase == null)
                return BuildingGrade.Enum.None;

            return grade.gradeBase.type;
        }

        private void SendMessage(BasePlayer player, string message)
        {
            if (player == null)
                return;

            string finalMessage = string.Format("{0} {1}", _config.Messages.Prefix, message);
            
            player.SendConsoleCommand("chat.add", 2, PluginIcon, finalMessage);
        }

        private void SendMessageToConsole(string message)
        {
            Puts(string.Format("{0} {1}", Prefix, message));
        }

        private void PrintStartupBanner()
        {
            Puts("==================================================");
            Puts(string.Format("{0} loaded successfully.", Name));
            Puts(string.Format("Author: whitecristafer | Sponsored by infunv.ru"));
            Puts(string.Format("Version: {0}", PluginVersion));
            Puts(string.Format("Chat prefix: {0}", Prefix));
            Puts("==================================================");
        }

        private string GetLogTag()
        {
            return "[BuildingLicense]";
        }

        private void LogBlocked(BasePlayer player, BuildingBlock block, BuildingGrade.Enum grade, string reason)
        {
            if (!_config.Settings.LogToConsole)
                return;

            string playerName = player != null ? player.displayName : "unknown";
            string userId = player != null ? player.UserIDString : "0";
            string prefab = "unknown";

            if (block != null)
            {
                try
                {
                    prefab = block.ShortPrefabName;
                }
                catch
                {
                    prefab = block.name;
                }
            }
        }

        #endregion

        #region Enums

        private enum LicenseTier
        {
            Stone,
            Metal,
            Armored
        }

        #endregion
    }
}