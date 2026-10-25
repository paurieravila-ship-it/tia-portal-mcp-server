using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Principal;
using Siemens.Engineering;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.HmiUnified;
using Siemens.Engineering.HmiUnified.UI.Dynamization;
using Siemens.Engineering.HmiUnified.UI.Dynamization.Script;
using Siemens.Engineering.HmiUnified.UI.Widgets;
using Siemens.Engineering.SW;
using TiaMcpServer.Mcp;

namespace TiaMcpServer
{
    // Connection, project lifecycle and environment diagnostics.
    internal sealed partial class TiaService
    {
        // ---- Connection --------------------------------------------------------

        public object Connect(string mode, string projectPath)
        {
            // The stored handles may be dead: the user closed the project, opened
            // another one, or quit TIA Portal. Drop them before deciding, or every
            // later call would keep failing with "access to a disposed object".
            RefreshSession();

            var attachMode = (mode ?? "attach").ToLowerInvariant() == "attach";

            // Attached to an instance with no project (e.g. an empty TIA window
            // picked by mistake when several are running): drop it and re-attach.
            if (_tia != null && _project == null && attachMode)
            {
                try { _tia.Dispose(); } catch { }
                _tia = null;
            }

            // Several projects open in separate TIA instances: switch when the
            // caller asks for a project other than the attached one.
            if (_tia != null && attachMode && !string.IsNullOrWhiteSpace(projectPath))
            {
                var current = Try(() => _project?.Path?.FullName);
                if (!string.Equals(current, Path.GetFullPath(projectPath), StringComparison.OrdinalIgnoreCase))
                {
                    _project = null;
                    try { _tia.Dispose(); } catch { }
                    _tia = null;
                }
            }

            if (_tia != null)
                return State();

            switch ((mode ?? "attach").ToLowerInvariant())
            {
                case "attach":
                    var running = TiaPortal.GetProcesses();
                    if (running.Count == 0)
                        throw new InvalidOperationException(
                            "No running TIA Portal instance found. Open TIA Portal first, or use mode \"new_ui\".");

                    // Several instances may be running (empty windows, leftover
                    // headless ones): take the one holding the requested project,
                    // else the first one that has any project open.
                    TiaPortalProcess target = null;
                    if (!string.IsNullOrWhiteSpace(projectPath))
                    {
                        var wanted = Path.GetFullPath(projectPath);
                        target = running.FirstOrDefault(p => Try(() =>
                            p.ProjectPath != null &&
                            p.ProjectPath.FullName.Equals(wanted, StringComparison.OrdinalIgnoreCase)));
                        if (target == null)
                            throw new InvalidOperationException(
                                $"No running TIA Portal instance has '{wanted}' open. " +
                                "Open it in TIA Portal, or call tia_connect without projectPath.");
                    }
                    target = target
                             ?? running.FirstOrDefault(p => Try(() => p.ProjectPath != null))
                             ?? running[0];

                    try
                    {
                        _tia = target.Attach();
                    }
                    catch (EngineeringSecurityException ex)
                    {
                        throw new InvalidOperationException(
                            "TIA Portal refused the connection: " + Brief(ex.Message) + " " +
                            "Two usual causes: (1) TIA Portal is showing an 'Openness access' prompt for this server " +
                            "and nobody answered it - ask the user to click 'Yes to all' in the TIA Portal window, then " +
                            "call tia_connect again (the prompt returns every time the server exe is rebuilt); " +
                            "(2) the Windows user is not in the local 'Siemens TIA Openness' group - see tia_doctor.");
                    }
                    _project = _tia.Projects.FirstOrDefault();
                    break;

                case "new_ui":
                    _tia = new TiaPortal(TiaPortalMode.WithUserInterface);
                    break;

                case "new_headless":
                    _tia = new TiaPortal(TiaPortalMode.WithoutUserInterface);
                    break;

                default:
                    throw new ArgumentException($"Unknown mode '{mode}'. Use attach | new_ui | new_headless.");
            }

            return State();
        }

        public object State()
        {
            RefreshSession();
            var o = ServerOptions.Current;

            return new
            {
                connected = _tia != null,
                projectOpen = _project != null,
                projectName = _project?.Name,
                projectPath = _project?.Path?.FullName,
                unsavedChanges = _project == null ? (bool?)null : Try(() => (bool?)_project.IsModified),
                server = new
                {
                    version = McpServer.ServerVersion,
                    access = o.Access.ToString().ToLowerInvariant(),
                    groups = o.Groups.OrderBy(g => g).ToArray(),
                    saveAllowed = o.AllowSave,
                    undoTransactions = o.Transactions,
                    automaticBackups = o.Backups,
                    backupDir = o.Backups ? o.EffectiveBackupDir : null,
                    allowedDirs = o.AllowedDirs.Count == 0 ? null : o.AllowedDirs.ToArray()
                }
            };
        }

        // ---- Project -----------------------------------------------------------

        public object OpenProject(string path)
        {
            EnsureTia();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new FileNotFoundException($"Project file not found: {path}");

            _project = _tia.Projects.Open(new FileInfo(path));
            return State();
        }

        private static void EnsureSavingAllowed()
        {
            if (!ServerOptions.Current.AllowSave)
                throw new UnauthorizedAccessException(
                    "Saving is disabled on this server (--no-save): the changes stay in TIA Portal's memory until " +
                    "the user saves the project there (Ctrl+S). Ask the user to review and save.");
        }

        public object SaveProject()
        {
            EnsureSavingAllowed();
            EnsureProject();
            _project.Save();
            return new { saved = true, projectName = _project.Name, path = _project.Path?.FullName };
        }

        public object CloseProject(bool saveFirst)
        {
            if (saveFirst) EnsureSavingAllowed();
            EnsureProject();
            var name = _project.Name;
            var hadUnsaved = Try(() => _project.IsModified);
            if (saveFirst) _project.Save();
            _project.Close();
            _project = null;
            return new
            {
                closed = true,
                projectName = name,
                saved = saveFirst,
                unsavedChangesDiscarded = hadUnsaved && !saveFirst
            };
        }

        public object ProjectInfo()
        {
            EnsureProject();
            var p = _project;

            var languages = Try(() => p.LanguageSettings.ActiveLanguages
                .Select(l => l.Culture.Name).ToArray(), new string[0]);

            return new
            {
                name = p.Name,
                path = p.Path?.FullName,
                author = Try(() => p.Author),
                version = Try(() => p.Version),
                created = Try(() => (DateTime?)p.CreationTime),
                lastModified = Try(() => (DateTime?)p.LastModified),
                lastModifiedBy = Try(() => p.LastModifiedBy),
                unsavedChanges = Try(() => (bool?)p.IsModified),
                sizeKb = Try(() => (long?)p.Size),
                languages = new
                {
                    active = languages,
                    editing = Try(() => p.LanguageSettings.EditingLanguage?.Culture?.Name),
                    reference = Try(() => p.LanguageSettings.ReferenceLanguage?.Culture?.Name)
                },
                usedProducts = Try(() => p.UsedProducts
                    .Select(u => new { name = u.Name, version = u.Version }).ToArray()),
                devices = AllDevices(p).Select(d => d.Name).ToArray()
            };
        }

        /// <summary>
        /// Writes a .zapNN archive of the project. Used as a restore point before large
        /// or destructive edits. TIA only archives a project without unsaved changes.
        /// </summary>
        public object ArchiveProject(string targetDir, string name, bool compressed)
        {
            EnsureProject();

            if (Try(() => _project.IsModified))
                throw new InvalidOperationException(
                    "The project has unsaved changes and TIA Portal only archives a saved project. Nothing was " +
                    "archived. Take the archive BEFORE editing; now the choice is the user's: save first " +
                    "(tia_save_project makes every pending change permanent) or carry on without an archive.");

            var projectDir = _project.Path.Directory.FullName;
            if (string.IsNullOrWhiteSpace(targetDir))
                targetDir = Path.Combine(ServerOptions.Current.EffectiveBackupDir, SafeFileName(_project.Name), "archives");

            var target = new DirectoryInfo(Path.GetFullPath(targetDir));
            if ((target.FullName + Path.DirectorySeparatorChar)
                .StartsWith(projectDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The archive directory must be outside the project folder.");
            target.Create();

            // ".ap20" project -> ".zap20" archive
            var extension = ".z" + _project.Path.Extension.TrimStart('.');
            var archiveName = string.IsNullOrWhiteSpace(name)
                ? $"{_project.Name}_{DateTime.Now:yyyyMMdd_HHmmss}"
                : SafeFileName(name);
            if (!archiveName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                archiveName += extension;

            _project.Archive(target, archiveName,
                compressed ? ProjectArchivationMode.Compressed : ProjectArchivationMode.None);

            var file = new FileInfo(Path.Combine(target.FullName, archiveName));
            return new
            {
                archived = true,
                path = file.FullName,
                sizeKb = file.Exists ? file.Length / 1024 : (long?)null,
                note = "Restore with TIA Portal: Project > Retrieve."
            };
        }

        public object InspectOtherProject(string path)
        {
            EnsureTia();
            var file = new FileInfo(path);
            var project = _tia.Projects.FirstOrDefault(p =>
                p.Path != null && p.Path.FullName.Equals(file.FullName, StringComparison.OrdinalIgnoreCase));
            if (project == null)
                project = _tia.Projects.Open(file);

            var hmi = SoftwareEntries<HmiSoftware>(project).FirstOrDefault()?.Software;
            object hmiInfo = null;
            if (hmi != null)
            {
                hmiInfo = new
                {
                    name = hmi.Name,
                    connections = hmi.Connections.Select(c => new
                    {
                        name = c.Name,
                        communicationDriver = c.CommunicationDriver,
                        initialAddress = c.InitialAddress,
                        disabledAtStartup = c.DisabledAtStartup,
                        node = SafeGet(() => c.Node),
                        partner = SafeGet(() => c.Partner),
                        station = SafeGet(() => c.Station)
                    }).ToArray(),
                    tags = hmi.TagTables.SelectMany(tt => tt.Tags).Select(t => new
                    {
                        name = t.Name,
                        connection = t.Connection,
                        plcTag = t.PlcTag,
                        dataType = t.DataType,
                        accessMode = t.AccessMode.ToString()
                    }).ToArray(),
                    screens = AllUnifiedScreens(hmi).Select(s => new
                    {
                        name = s.Name,
                        items = s.ScreenItems.Select(item => new
                        {
                            name = item.Name,
                            type = item.GetType().Name,
                            dynamizations = item.Dynamizations.Select(d => new
                            {
                                property = d.PropertyName,
                                dynamizationType = d.DynamizationType.ToString(),
                                scriptCode = (d as ScriptDynamization)?.ScriptCode,
                                tagBinding = (d as TagDynamization)?.Tag
                            }).ToArray(),
                            events = (item as HmiButton)?.EventHandlers.Select(eh => new
                            {
                                eventType = eh.EventType.ToString(),
                                scriptCode = eh.Script?.ScriptCode
                            }).ToArray()
                        }).ToArray()
                    }).ToArray()
                };
            }

            object plcInfo = null;
            var plcEntry = SoftwareEntries<PlcSoftware>(project).FirstOrDefault();
            if (plcEntry != null)
            {
                var accessProvider = plcEntry.Item.GetService<PlcAccessLevelProvider>();
                plcInfo = new
                {
                    name = plcEntry.Software.Name,
                    typeIdentifier = plcEntry.Item.TypeIdentifier,
                    protectionAccessLevel = accessProvider?.PlcProtectionAccessLevel.ToString(),
                    communicationMode = SafeGet(() => plcEntry.Item.GetAttribute("CommunicationMode")?.ToString())
                };
            }

            return new { projectName = project.Name, hmi = hmiInfo, plc = plcInfo };
        }

        // ---- Environment check -------------------------------------------------

        /// <summary>
        /// Checks the prerequisites that most often stop a first connection: Openness
        /// installed, the Windows user in the "Siemens TIA Openness" group, and a
        /// running TIA Portal. Works without being connected.
        /// </summary>
        public object Doctor()
        {
            var checks = new List<object>();
            void Check(string name, bool ok, string detail, string fix = null) =>
                checks.Add(new { check = name, ok, detail, fix = ok ? null : fix });

            var dllDir = OpennessResolver.DllDirectory;
            var dll = dllDir == null ? null : Path.Combine(dllDir, "Siemens.Engineering.dll");
            Check("Openness API installed", dll != null && File.Exists(dll), dll ?? "(not found)",
                "Install TIA Portal V20 with the 'TIA Openness' option selected.");

            bool inGroup = false;
            string user = null;
            try
            {
                var identity = WindowsIdentity.GetCurrent();
                user = identity.Name;
                inGroup = identity.Groups != null && identity.Groups.Any(sid =>
                    Try(() => sid.Translate(typeof(NTAccount)).Value, "")
                        .EndsWith("Siemens TIA Openness", StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex) { user = "ERR:" + ex.Message; }
            Check("User is in the 'Siemens TIA Openness' group", inGroup, user,
                "lusrmgr.msc > Groups > Siemens TIA Openness > add your user, then sign out and back in.");

            object instances = null;
            try
            {
                var running = TiaPortal.GetProcesses();
                instances = running.Select(p => new
                {
                    processId = p.Id,
                    mode = Try(() => p.Mode.ToString()),
                    project = Try(() => p.ProjectPath?.FullName)
                }).ToArray();
                Check("TIA Portal is running", running.Count > 0, $"{running.Count} instance(s)",
                    "Start TIA Portal and open your project, or connect with mode new_ui / new_headless.");
            }
            catch (Exception ex)
            {
                Check("TIA Portal is running", false, Brief(ex.Message),
                    "Could not query TIA Portal processes; check the two items above first.");
            }

            RefreshSession();
            Check("Connected to TIA Portal", _tia != null, _tia != null ? "attached" : "not connected", "Call tia_connect.");
            Check("Project open", _project != null, _project?.Name ?? "none",
                "Open a project in TIA Portal and call tia_connect, or use tia_open_project.");

            return new
            {
                server = McpServer.ServerName + " " + McpServer.ServerVersion,
                checks,
                tiaInstances = instances,
                note = "The first connection from a new build of this server makes TIA Portal show an " +
                       "'Openness access' prompt; answer 'Yes to all' in the TIA Portal window."
            };
        }
    }
}
