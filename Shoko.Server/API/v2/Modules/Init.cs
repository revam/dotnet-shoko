using System.Collections.Generic;
using System;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.SqlServer.Management.Smo;
using Nancy;
using Nancy.ModelBinding;
using Pri.LongPath;
using Shoko.Commons;
using Shoko.Models.Client;
using Shoko.Models.Server;
using Shoko.Server.API.v2.Models.core;
using Shoko.Server.Databases;
using Shoko.Server.Utilities;
using ServerStatus = Shoko.Server.API.v2.Models.core.ServerStatus;
using Settings = Shoko.Server.API.v2.Models.core.Settings;

namespace Shoko.Server.API.v2.Modules
{
    // ReSharper disable once UnusedMember.Global
    public class Init : NancyModule
    {
        /// <inheritdoc />
        /// <summary>
        /// Preinit Module for connection testing and setup
        /// Settings will be loaded prior to this starting
        /// Unless otherwise noted, these will only work before server init
        /// </summary>
        public Init() : base("/api/init")
        {
            // Get version, regardless of server status
            // This will work after init
            Get["/version", true] = async (x,ct) => await Task.Factory.StartNew(GetVersion, ct);

            // Get the startup state
            // This will work after init
            Get["/status", true] = async (x, ct) => await Task.Factory.StartNew(GetServerStatus, ct);

            // Get the Default User Credentials
            Get["/defaultuser", true] = async (x, ct) => await Task.Factory.StartNew(GetDefaultUserCredentials, ct);

            // Set the Default User Credentials
            // Pass this a Credentials object
            Post["/defaultuser", true] = async (x, ct) => await Task.Factory.StartNew(SetDefaultUserCredentials, ct);

            // Set AniDB user/pass
            // Pass this a Credentials object
            Post["/anidb", true] = async (x,ct) => await Task.Factory.StartNew(SetAniDB, ct);

            // Get existing AniDB user, don't provide pass
            Get["/anidb", true] = async (x,ct) => await Task.Factory.StartNew(GetAniDB, ct);

            // Test AniDB login
            Get["/anidb/test", true] = async (x,ct) => await Task.Factory.StartNew(TestAniDB, ct);

            // Get Database Settings
            Get["/database", true] = async (x,ct) => await Task.Factory.StartNew(GetDatabaseSettings, ct);

            // Set Database Settings
            Post["/database", true] = async (x,ct) => await Task.Factory.StartNew(SetDatabaseSettings, ct);

            // Test Database Connection
            Get["/database/test", true] = async (x,ct) => await Task.Factory.StartNew(TestDatabaseConnection, ct);

            // Get SQL Server Instances on the Machine
            Get["/database/sqlserverinstance", true] = async (x,ct) => await Task.Factory.StartNew(GetMSSQLInstances, ct);

            // Get the whole settings file
            Get["/config", true] = async (x,ct) => await Task.Factory.StartNew(ExportConfig, ct);

            // Replace the whole settings file
            Post["/config", true] = async (x,ct) => await Task.Factory.StartNew(ImportConfig, ct);

            // Get a single setting value
            Get["/setting", true] = async (x, ct) => await Task.Factory.StartNew(GetSetting, ct);

            // Set a single setting value
            Patch["/setting", true] = async (x, ct) => await Task.Factory.StartNew(SetSetting, ct);

            // Start the server
            Get["/startserver", true] = async (x, ct) => await Task.Factory.StartNew(StartServer, ct);
        }

        /// <summary>
        /// Return current version of ShokoServer and several modules
        /// This will work after init
        /// </summary>
        /// <returns></returns>
        private object GetVersion()
        {
            List<ComponentVersion> list = new List<ComponentVersion>();

            ComponentVersion version = new ComponentVersion
            {
                version = Utils.GetApplicationVersion(),
                name = "server"
            };
            list.Add(version);

            string versionExtra = Utils.GetApplicationExtraVersion();

            if (!string.IsNullOrEmpty(versionExtra))
            {
                version = new ComponentVersion
                {
                    version = versionExtra,
                    name = "servercommit"
                };
                list.Add(version);
            }

            version = new ComponentVersion
            {
                version = Assembly.GetAssembly(typeof(FolderMappings)).GetName().Version.ToString(),
                name = "commons"
            };
            list.Add(version);

            version = new ComponentVersion
            {
                version = Assembly.GetAssembly(typeof(AniDB_Anime)).GetName().Version.ToString(),
                name = "models"
            };
            list.Add(version);

            version = new ComponentVersion
            {
                version = Assembly.GetAssembly(typeof(INancyModule)).GetName().Version.ToString(),
                name = "Nancy"
            };
            list.Add(version);

            string dllpath = Assembly.GetEntryAssembly().Location;
            dllpath = Path.GetDirectoryName(dllpath);
            dllpath = Path.Combine(dllpath, "x86");
            dllpath = Path.Combine(dllpath, "MediaInfo.dll");

            if (File.Exists(dllpath))
            {
                version = new ComponentVersion
                {
                    version = FileVersionInfo.GetVersionInfo(dllpath).FileVersion,
                    name = "MediaInfo"
                };
                list.Add(version);
            }
            else
            {
                dllpath = Assembly.GetEntryAssembly().Location;
                dllpath = Path.GetDirectoryName(dllpath);
                dllpath = Path.Combine(dllpath, "x64");
                dllpath = Path.Combine(dllpath, "MediaInfo.dll");
                if (File.Exists(dllpath))
                {
                    version = new ComponentVersion
                    {
                        version = FileVersionInfo.GetVersionInfo(dllpath).FileVersion,
                        name = "MediaInfo"
                    };
                    list.Add(version);
                }
                else
                {
                    version = new ComponentVersion
                    {
                        version = @"DLL not found, using internal",
                        name = "MediaInfo"
                    };
                    list.Add(version);
                }
            }

            if (File.Exists("webui//index.ver"))
            {
                string webui_version = File.ReadAllText("webui//index.ver");
                string[] versions = webui_version.Split('>');
                if (versions.Length == 2)
                {
                    version = new ComponentVersion
                    {
                        name = "webui/" + versions[0],
                        version = versions[1]
                    };
                    list.Add(version);
                }
            }

            return list;
        }

        /// <summary>
        /// Gets various information about the startup status of the server
        /// This will work after init
        /// </summary>
        /// <returns></returns>
        private object GetServerStatus()
        {
            ServerStatus status = new ServerStatus
            {
                server_started = ServerState.Instance.ServerOnline,
                startup_state = ServerState.Instance.CurrentSetupStatus,
                first_run = ServerSettings.FirstRun,
                startup_failed = ServerState.Instance.StartupFailed,
                startup_failed_error_message = ServerState.Instance.StartupFailedMessage
            };
            return status;
        }

        /// <summary>
        /// Gets the Default user's credentials. Will only return on first run
        /// </summary>
        /// <returns></returns>
        private object GetDefaultUserCredentials()
        {
            if (!ServerSettings.FirstRun || ServerState.Instance.ServerOnline || ServerState.Instance.ServerStarting)
                return APIStatus.BadRequest("You may only request the default user's credentials on first run");

            return new Credentials
            {
                login = ServerSettings.DefaultUserUsername,
                password = ServerSettings.DefaultUserPassword
            };
        }

        /// <summary>
        /// Sets the default user's credentials
        /// </summary>
        /// <returns></returns>
        private object SetDefaultUserCredentials()
        {
            if (!ServerSettings.FirstRun || ServerState.Instance.ServerOnline || ServerState.Instance.ServerStarting)
                return APIStatus.BadRequest("You may only set the default user's credentials on first run");

            try
            {
                Credentials credentials = this.Bind();
                ServerSettings.DefaultUserUsername = credentials.login;
                ServerSettings.DefaultUserPassword = credentials.password;
                return APIStatus.OK();
            }
            catch
            {
                return APIStatus.InternalError();
            }
        }

        /// <summary>
        /// Starts the server, or does nothing
        /// </summary>
        /// <returns></returns>
        private object StartServer()
        {
            if (ServerState.Instance.ServerOnline) return APIStatus.BadRequest("Already Running");
            if (ServerState.Instance.ServerStarting) return APIStatus.BadRequest("Already Starting");
            ShokoServer.RunWorkSetupDB();
            return APIStatus.OK();
        }

        #region 01. AniDB

        /// <summary>
        /// Set AniDB account credentials with a Credentials object
        /// </summary>
        /// <returns></returns>
        private object SetAniDB()
        {
            if (ServerState.Instance.ServerOnline || ServerState.Instance.ServerStarting)
                return APIStatus.BadRequest("You may only do this before server init");

            Credentials cred = this.Bind();
            if (string.IsNullOrEmpty(cred.login) || string.IsNullOrEmpty(cred.password))
                return new APIMessage(400, "Login and Password missing");

            ServerSettings.AniDB_Username = cred.login;
            ServerSettings.AniDB_Password = cred.password;
            if (cred.port != 0)
                ServerSettings.AniDB_ClientPort = cred.port.ToString();
            if (!string.IsNullOrEmpty(cred.apikey))
                ServerSettings.AniDB_AVDumpKey = cred.apikey;
            if (cred.apiport != 0)
                ServerSettings.AniDB_AVDumpClientPort = cred.apiport.ToString();

            return APIStatus.OK();
        }

        /// <summary>
        /// Test AniDB Creditentials
        /// </summary>
        /// <returns></returns>
        private object TestAniDB()
        {
            if (ServerState.Instance.ServerOnline || ServerState.Instance.ServerStarting)
                return APIStatus.BadRequest("You may only do this before server init");

            ShokoService.AnidbProcessor.ForceLogout();
            ShokoService.AnidbProcessor.CloseConnections();

            Thread.Sleep(1000);

            Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo(ServerSettings.Culture);

            ShokoService.AnidbProcessor.Init(ServerSettings.AniDB_Username, ServerSettings.AniDB_Password,
                ServerSettings.AniDB_ServerAddress,
                ServerSettings.AniDB_ServerPort, ServerSettings.AniDB_ClientPort);

            if (!ShokoService.AnidbProcessor.Login()) return APIStatus.Unauthorized();
            ShokoService.AnidbProcessor.ForceLogout();

            return APIStatus.OK();
        }

        /// <summary>
        /// Return existing login and ports for AniDB
        /// </summary>
        /// <returns></returns>
        private object GetAniDB()
        {
            if (ServerState.Instance.ServerOnline || ServerState.Instance.ServerStarting)
                return APIStatus.BadRequest("You may only do this before server init");

            try
            {
                Credentials cred = new Credentials
                {
                    login = ServerSettings.AniDB_Username,
                    port = int.Parse(ServerSettings.AniDB_ClientPort),
                    apiport = int.Parse(ServerSettings.AniDB_AVDumpClientPort)
                };
                return cred;
            }
            catch
            {
                return APIStatus.InternalError(
                    "The ports are not set as integers. Set them and try again.\n\rThe default values are:\n\rAniDB Client Port: 4556\n\rAniDB AVDump Client Port: 4557");
            }
        }

        #endregion

        #region 02. Database

        /// <summary>
        /// Get Database Settings
        /// </summary>
        /// <returns></returns>
        private object GetDatabaseSettings()
        {
            if (ServerState.Instance.ServerOnline || ServerState.Instance.ServerStarting)
                return APIStatus.BadRequest("You may only do this before server init");

            var settings = new DatabaseSettings
            {
                db_type = ServerSettings.DatabaseType,
                mysql_hostname = ServerSettings.MySQL_Hostname,
                mysql_password = ServerSettings.MySQL_Password,
                mysql_schemaname = ServerSettings.MySQL_SchemaName,
                mysql_username = ServerSettings.MySQL_Username,
                sqlite_databasefile = ServerSettings.DatabaseFile,
                sqlserver_databasename = ServerSettings.DatabaseName,
                sqlserver_databaseserver = ServerSettings.DatabaseServer,
                sqlserver_password = ServerSettings.DatabasePassword,
                sqlserver_username = ServerSettings.DatabaseUsername
            };

            return settings;
        }

        /// <summary>
        /// Set Database Settings
        /// </summary>
        /// <returns></returns>
        private object SetDatabaseSettings()
        {
            if (ServerState.Instance.ServerOnline || ServerState.Instance.ServerStarting)
                return APIStatus.BadRequest("You may only do this before server init");

            DatabaseSettings settings = this.Bind();
            string dbtype = settings.db_type.Trim();
            if (dbtype.Equals(Constants.DatabaseType.MySQL, StringComparison.InvariantCultureIgnoreCase))
            {
                if (string.IsNullOrEmpty(settings.mysql_hostname) || string.IsNullOrEmpty(settings.mysql_password) ||
                    string.IsNullOrEmpty(settings.mysql_schemaname) || string.IsNullOrEmpty(settings.mysql_username))
                    return APIStatus.BadRequest("An invalid setting was passed");
                ServerSettings.DatabaseType = Constants.DatabaseType.MySQL;
                ServerSettings.MySQL_Hostname = settings.mysql_hostname;
                ServerSettings.MySQL_Password = settings.mysql_password;
                ServerSettings.MySQL_SchemaName = settings.mysql_schemaname;
                ServerSettings.MySQL_Username = settings.mysql_username;
                return APIStatus.OK();
            }
            if (dbtype.Equals(Constants.DatabaseType.SqlServer, StringComparison.InvariantCultureIgnoreCase))
            {
                if (string.IsNullOrEmpty(settings.sqlserver_databasename) || string.IsNullOrEmpty(settings.sqlserver_databaseserver) ||
                    string.IsNullOrEmpty(settings.sqlserver_password) || string.IsNullOrEmpty(settings.sqlserver_username))
                    return APIStatus.BadRequest("An invalid setting was passed");
                ServerSettings.DatabaseType = Constants.DatabaseType.SqlServer;
                ServerSettings.DatabaseServer = settings.sqlserver_databaseserver;
                ServerSettings.DatabaseName = settings.sqlserver_databasename;
                ServerSettings.DatabaseUsername = settings.sqlserver_username;
                ServerSettings.DatabasePassword = settings.sqlserver_password;
                return APIStatus.OK();
            }
            if (dbtype.Equals(Constants.DatabaseType.Sqlite, StringComparison.InvariantCultureIgnoreCase))
            {
                if (string.IsNullOrEmpty(settings.sqlite_databasefile))
                    return APIStatus.BadRequest("An invalid setting was passed");
                ServerSettings.DatabaseType = Constants.DatabaseType.Sqlite;
                ServerSettings.DatabaseFile = settings.sqlite_databasefile;
                return APIStatus.OK();
            }
            return APIStatus.BadRequest("An invalid setting was passed");
        }

        /// <summary>
        /// Test Database Connection with Current Settings
        /// </summary>
        /// <returns>200 if connection successful, 400 otherwise</returns>
        private object TestDatabaseConnection()
        {
            if (ServerState.Instance.ServerOnline || ServerState.Instance.ServerStarting)
                return APIStatus.BadRequest("You may only do this before server init");

            if (ServerSettings.DatabaseType.Equals(Constants.DatabaseType.MySQL,
                    StringComparison.InvariantCultureIgnoreCase) && new MySQL().TestConnection())
                return APIStatus.OK();

            if (ServerSettings.DatabaseType.Equals(Constants.DatabaseType.SqlServer,
                    StringComparison.InvariantCultureIgnoreCase) && new SQLServer().TestConnection())
                return APIStatus.OK();

            if (ServerSettings.DatabaseType.Equals(Constants.DatabaseType.Sqlite,
                StringComparison.InvariantCultureIgnoreCase))
                return APIStatus.OK();

            return APIStatus.BadRequest("Failed to Connect");
        }

        /// <summary>
        /// Get SQL Server Instances Running on this Machine
        /// </summary>
        /// <returns>List of strings that may be passed as sqlserver_databaseserver</returns>
        private object GetMSSQLInstances()
        {
            if (ServerState.Instance.ServerOnline || ServerState.Instance.ServerStarting)
                return APIStatus.BadRequest("You may only do this before server init");

            List<string> instances = new List<string>();

            DataTable dt = SmoApplication.EnumAvailableSqlServers();
            if (dt?.Rows.Count > 0) instances.AddRange(from DataRow row in dt.Rows select row[0].ToString());

            return instances;
        }
        #endregion

        #region 03. Settings

        /// <summary>
        /// Return body of current working settings.json - this could act as backup
        /// </summary>
        /// <returns></returns>
        private object ExportConfig()
        {
            if (ServerState.Instance.ServerOnline || ServerState.Instance.ServerStarting)
                return APIStatus.BadRequest("You may only do this before server init");

            try
            {
                return ServerSettings.appSettings;
            }
            catch
            {
                return APIStatus.InternalError("Error while reading settings.");
            }
        }

        /// <summary>
        /// Import config file that was sent to in API body - this act as import from backup
        /// </summary>
        /// <returns>APIStatus</returns>
        private object ImportConfig()
        {
            if (ServerState.Instance.ServerOnline || ServerState.Instance.ServerStarting)
                return APIStatus.BadRequest("You may only do this before server init");

            CL_ServerSettings settings = this.Bind();
            string raw_settings = settings.ToJSON();

            if (raw_settings.Length == new CL_ServerSettings().ToJSON().Length)
                return APIStatus.BadRequest("Empty settings are not allowed");

            string path = Path.Combine(ServerSettings.ApplicationPath, "temp.json");
            File.WriteAllText(path, raw_settings, System.Text.Encoding.UTF8);
            try
            {
                ServerSettings.LoadSettingsFromFile(path, true);
                return APIStatus.OK();
            }
            catch
            {
                return APIStatus.InternalError("Error while importing settings");
            }
        }

        /// <summary>
        /// Return given setting
        /// </summary>
        /// <returns></returns>
        private object GetSetting()
        {
            if (ServerState.Instance.ServerOnline || ServerState.Instance.ServerStarting)
                return APIStatus.BadRequest("You may only do this before server init");

            try
            {
                // TODO Refactor Settings to a POCO that is serialized, and at runtime, build a dictionary of types to validate against
                Settings setting = this.Bind();
                if (string.IsNullOrEmpty(setting?.setting)) return APIStatus.BadRequest("An invalid setting was passed");
                try
                {
                    var value = typeof(ServerSettings).GetProperty(setting.setting)?.GetValue(null, null);
                    if (value == null) return APIStatus.BadRequest("An invalid setting was passed");

                    Settings return_setting = new Settings
                    {
                        setting = setting.setting,
                        value = value.ToString()
                    };
                    return return_setting;
                }
                catch
                {
                    return APIStatus.BadRequest("An invalid setting was passed");
                }
            }
            catch
            {
                return APIStatus.InternalError();
            }
        }

        /// <summary>
        /// Set given setting
        /// </summary>
        /// <returns></returns>
        private object SetSetting()
        {
            if (ServerState.Instance.ServerOnline || ServerState.Instance.ServerStarting)
                return APIStatus.BadRequest("You may only do this before server init");

            // TODO Refactor Settings to a POCO that is serialized, and at runtime, build a dictionary of types to validate against
            try
            {
                Settings setting = this.Bind();
                if (string.IsNullOrEmpty(setting.setting))
                    return APIStatus.BadRequest("An invalid setting was passed");

                if (setting.value == null) return APIStatus.BadRequest("An invalid value was passed");

                var property = typeof(ServerSettings).GetProperty(setting.setting);
                if (property == null) return APIStatus.BadRequest("An invalid setting was passed");
                if (!property.CanWrite) return APIStatus.BadRequest("An invalid setting was passed");
                var settingType = property.PropertyType;
                try
                {
                    var converter = TypeDescriptor.GetConverter(settingType);
                    if (!converter.CanConvertFrom(typeof(string)))
                        return APIStatus.BadRequest("An invalid value was passed");
                    var value = converter.ConvertFromInvariantString(setting.value);
                    if (value == null) return APIStatus.BadRequest("An invalid value was passed");
                    property.SetValue(null, value);
                }
                catch
                {
                    // ignore, we are returning the error below
                }

                return APIStatus.BadRequest("An invalid value was passed");
            }
            catch
            {
                return APIStatus.InternalError();
            }
        }

        #endregion
    }
}