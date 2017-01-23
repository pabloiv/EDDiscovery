﻿/*
 * Copyright © 2015 - 2017 EDDiscovery development team
 *
 * Licensed under the Apache License, Version 2.0 (the "License"); you may not use this
 * file except in compliance with the License. You may obtain a copy of the License at
 *
 * http://www.apache.org/licenses/LICENSE-2.0
 * 
 * Unless required by applicable law or agreed to in writing, software distributed under
 * the License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF
 * ANY KIND, either express or implied. See the License for the specific language
 * governing permissions and limitations under the License.
 * 
 * EDDiscovery is not affiliated with Fronter Developments plc.
 */
using EDDiscovery.DB;
using EDDiscovery2;
using EDDiscovery2.DB;
using EDDiscovery2.EDSM;
using EDDiscovery2.Forms;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using System.Configuration;
using EDDiscovery.EDSM;
using System.Threading.Tasks;
using System.Text;
using System.ComponentModel;
using System.Text.RegularExpressions;
using EDDiscovery.HTTP;
using EDDiscovery.Forms;
using EDDiscovery.EliteDangerous;
using EDDiscovery.EliteDangerous.JournalEvents;
using EDDiscovery.EDDN;

namespace EDDiscovery
{

    public delegate void DistancesLoaded();

    public partial class EDDiscoveryForm : Form
    {
        #region Variables

        public const int WM_MOVE = 3;
        public const int WM_SIZE = 5;
        public const int WM_MOUSEMOVE = 0x200;
        public const int WM_LBUTTONDOWN = 0x201;
        public const int WM_LBUTTONUP = 0x202;
        public const int WM_NCLBUTTONDOWN = 0xA1;
        public const int WM_NCLBUTTONUP = 0xA2;
        public const int WM_NCMOUSEMOVE = 0xA0;
        public const int HT_CLIENT = 0x1;
        public const int HT_CAPTION = 0x2;
        public const int HT_LEFT = 0xA;
        public const int HT_RIGHT = 0xB;
        public const int HT_BOTTOM = 0xF;
        public const int HT_BOTTOMRIGHT = 0x11;
        public const int WM_NCL_RESIZE = 0x112;
        public const int HT_RESIZE = 61448;
        public const int WM_NCHITTEST = 0x84;

        // Mono compatibility
        private bool _window_dragging = false;
        private Point _window_dragMousePos = Point.Empty;
        private Point _window_dragWindowPos = Point.Empty;
        public EDDTheme theme;

        public HistoryList history = new HistoryList();

        static public EDDConfig EDDConfig { get; private set; }

        public TravelHistoryControl TravelControl { get { return travelHistoryControl1; } }
        public RouteControl RouteControl { get { return routeControl1; } }
        public ExportControl ExportControl { get { return exportControl1; } }
        public EDDiscovery2.ImageHandler.ImageHandler ImageHandler { get { return imageHandler1; } }

        public EDDiscovery2._3DMap.MapManager Map { get; private set; }

        public event EventHandler HistoryRefreshed; // this is an internal hook


        public delegate void HistoryChange(HistoryList l);          // subscribe to get events
        public event HistoryChange OnHistoryChange;
        public delegate void NewEntry(HistoryEntry l, HistoryList hl);
        public event NewEntry OnNewEntry;
        public delegate void NewJournalEntry(JournalEntry je);
        public event NewJournalEntry OnNewJournalEntry;
        public delegate void NewLogEntry(string txt, Color c);
        public event NewLogEntry OnNewLogEntry;
        public delegate void NewTarget();
        public event NewTarget OnNewTarget;

        public GalacticMapping galacticMapping;

        public Actions.ActionFileList actionfiles;
        public string actionfileskeyevents;
        ActionMessageFilter actionfilesmessagefilter;
        public Actions.ActionRun actionrunasync;
        private ConditionVariables internalglobalvariables;         // internally set variables, either program or user program ones
        private ConditionVariables usercontrolledglobalvariables;     // user variables, set by user only
        public ConditionVariables globalvariables;               // combo of above.

        public CancellationTokenSource CancellationTokenSource { get; private set; } = new CancellationTokenSource();

        private ManualResetEvent _syncWorkerCompletedEvent = new ManualResetEvent(false);
        private ManualResetEvent _checkSystemsWorkerCompletedEvent = new ManualResetEvent(false);

        public EDSMSync EdsmSync;

        Action cancelDownloadMaps = null;
        Task<bool> downloadMapsTask = null;
        Task checkInstallerTask = null;
        private bool themeok = true;
        private Forms.SplashForm splashform = null;
        BackgroundWorker dbinitworker = null;

        EliteDangerous.EDJournalClass journalmonitor;
        GitHubRelease newRelease;

        public PopOutControl PopOuts;

        private bool _formMax;
        private int _formWidth;
        private int _formHeight;
        private int _formTop;
        private int _formLeft;

        #endregion

        #region Initialisation

        public EDDiscoveryForm()
        {
            InitializeComponent();

            EDDConfig.Options.Init(ModifierKeys.HasFlag(Keys.Shift));

            label_version.Text = EDDConfig.Options.VersionDisplayString;

            if (EDDConfig.Options.ReadJournal != null)
            {
                EDJournalClass.ReadCmdLineJournal(EDDConfig.Options.ReadJournal);
            }

            string logpath = "";
            try
            {
                logpath = Path.Combine(Tools.GetAppDataDirectory(), "Log");
                if (!Directory.Exists(logpath))
                {
                    Directory.CreateDirectory(logpath);
                }

                if (!Debugger.IsAttached || EDDConfig.Options.TraceLog)
                {
                    TraceLog.LogFileWriterException += ex =>
                    {
                        LogLineHighlight($"Log Writer Exception: {ex}");
                    };
                    TraceLog.Init(EDDConfig.Options.LogExceptions);
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Unable to create the folder '{logpath}'");
                Trace.WriteLine($"Exception: {ex.Message}");
            }

            SQLiteConnectionUser.EarlyReadRegister();
            EDDConfig.Instance.Update(write: false);

            dbinitworker = new BackgroundWorker();
            dbinitworker.DoWork += Dbinitworker_DoWork;
            dbinitworker.RunWorkerCompleted += Dbinitworker_RunWorkerCompleted;
            dbinitworker.RunWorkerAsync();

            theme = new EDDTheme();

            EDDConfig = EDDConfig.Instance;
            galacticMapping = new GalacticMapping();

            PopOuts = new PopOutControl(this);

            ToolStripManager.Renderer = theme.toolstripRenderer;
            theme.LoadThemes();                                         // default themes and ones on disk loaded
            themeok = theme.RestoreSettings();                                    // theme, remember your saved settings

            trilaterationControl.InitControl(this);
            travelHistoryControl1.InitControl(this);
            imageHandler1.InitControl(this);
            settings.InitControl(this);
            journalViewControl1.InitControl(this, 0);
            routeControl1.InitControl(this);
            savedRouteExpeditionControl1.InitControl(this);
            exportControl1.InitControl(this);


            EdsmSync = new EDSMSync(this);

            Map = new EDDiscovery2._3DMap.MapManager(EDDConfig.Options.NoWindowReposition, this);

            journalmonitor = new EliteDangerous.EDJournalClass();

            this.TopMost = EDDConfig.KeepOnTop;

            ApplyTheme();

            history.CommanderId = EDDiscoveryForm.EDDConfig.CurrentCommander.Nr;

            notifyIcon1.Visible = EDDConfig.UseNotifyIcon;
        }

        private void Dbinitworker_DoWork(object sender, DoWorkEventArgs e)
        {
            Trace.WriteLine("Initializing database");
            SQLiteConnectionOld.Initialize();
            SQLiteConnectionUser.Initialize();
            SQLiteConnectionSystem.Initialize();
            Trace.WriteLine("Database initialization complete");
        }

        private void Dbinitworker_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            if (splashform != null)
            {
                splashform.Close();
            }
        }

        private void EDDiscoveryForm_Layout(object sender, LayoutEventArgs e)       // Manually position, could not get gripper under tab control with it sizing for the life of me
        {
        }

        private void EDDiscoveryForm_Load(object sender, EventArgs e)
        {
            try
            {
                usercontrolledglobalvariables = new ConditionVariables();
                usercontrolledglobalvariables.FromString(SQLiteConnectionUser.GetSettingString("UserGlobalActionVars", ""), ConditionVariables.FromMode.MultiEntryComma);

                globalvariables = new ConditionVariables(usercontrolledglobalvariables);        // copy existing user ones into to shared buffer..

                internalglobalvariables = new ConditionVariables();

                SetInternalGlobal("CurrentCulture", System.Threading.Thread.CurrentThread.CurrentCulture.Name);
                SetInternalGlobal("CurrentCultureInEnglish", System.Threading.Thread.CurrentThread.CurrentCulture.EnglishName);
                SetInternalGlobal("CurrentCultureISO", System.Threading.Thread.CurrentThread.CurrentCulture.ThreeLetterISOLanguageName);

                if (!(SQLiteConnectionUser.IsInitialized && SQLiteConnectionSystem.IsInitialized))
                {
                    splashform = new SplashForm();
                    splashform.ShowDialog(this);
                }

                EliteDangerousClass.CheckED();
                EDDConfig.Update();
                RepositionForm();
                InitFormControls();
                settings.InitSettingsTab();
                savedRouteExpeditionControl1.LoadControl();
                travelHistoryControl1.LoadControl();

                CheckIfEliteDangerousIsRunning();

                if (EDDConfig.Options.Debug)
                {
                    button_test.Visible = true;
                }

                StartUpActions();
            }
            catch (Exception ex)
            {
                System.Windows.Forms.MessageBox.Show("EDDiscoveryForm_Load exception: " + ex.Message + "\n" + "Trace: " + ex.StackTrace);
            }
        }

        internal void SaveCurrentPopOuts()
        {
            PopOuts.SaveCurrentPopouts();
        }

        internal void LoadSavedPopouts()
        {
            PopOuts.LoadSavedPopouts();
        }

        private void EDDiscoveryForm_Shown(object sender, EventArgs e)
        {
            _checkSystemsWorker.RunWorkerAsync();
            downloadMapsTask = FGEImage.DownloadMaps(this, (cb) => cancelDownloadMaps = cb, LogLine, LogLineHighlight);

            if (!themeok)
            {
                LogLineHighlight("The theme stored has missing colors or other missing information");
                LogLineHighlight("Correct the missing colors or other information manually using the Theme Editor in Settings");
            }

            ActionRunOnEvent("onStartup", "ProgramEvent");
        }

        private Task CheckForNewInstallerAsync()
        {
            return Task.Factory.StartNew(() =>
            {
                CheckForNewinstaller();
            });
        }

        private bool CheckForNewinstaller()
        {
            try
            {

                GitHubClass github = new GitHubClass(this);

                GitHubRelease rel = github.GetLatestRelease();

                if (rel != null)
                {
                    //string newInstaller = jo["Filename"].Value<string>();

                    var currentVersion = Application.ProductVersion;

                    Version v1, v2;
                    v1 = new Version(rel.ReleaseVersion);
                    v2 = new Version(currentVersion);

                    if (v1.CompareTo(v2) > 0) // Test if newer installer exists:
                    {
                        newRelease = rel;
                        this.BeginInvoke(new Action(() => LogLineHighlight("New EDDiscovery installer available: " + rel.ReleaseName)));
                        this.BeginInvoke(new Action(() => PanelInfoNewRelease()));
                        return true;
                    }
                }
            }
            catch (Exception)
            {

            }

            return false;
        }

        private void PanelInfoNewRelease()
        {
            ShowInfoPanel("Download new release!", true, Color.Green);
        }


        private void InitFormControls()
        {
            ShowInfoPanel("Loading. Please wait!", true, Color.Gold);
            
            routeControl1.travelhistorycontrol1 = travelHistoryControl1;
        }

        private void RepositionForm()
        {
            var top = SQLiteDBClass.GetSettingInt("FormTop", -1);
            if (top != -1 && EDDConfig.Options.NoWindowReposition == false)
            {
                var left = SQLiteDBClass.GetSettingInt("FormLeft", 0);
                var height = SQLiteDBClass.GetSettingInt("FormHeight", 800);
                var width = SQLiteDBClass.GetSettingInt("FormWidth", 800);

                // Adjust so window fits on screen; just in case user unplugged a monitor or something

                var screen = SystemInformation.VirtualScreen;
                if (height > screen.Height) height = screen.Height;
                if (top + height > screen.Height + screen.Top) top = screen.Height + screen.Top - height;
                if (width > screen.Width) width = screen.Width;
                if (left + width > screen.Width + screen.Left) left = screen.Width + screen.Left - width;
                if (top < screen.Top) top = screen.Top;
                if (left < screen.Left) left = screen.Left;

                this.Top = top;
                this.Left = left;
                this.Height = height;
                this.Width = width;

                this.CreateParams.X = this.Left;
                this.CreateParams.Y = this.Top;
                this.StartPosition = FormStartPosition.Manual;

                _formMax = SQLiteDBClass.GetSettingBool("FormMax", false);
                if (_formMax) this.WindowState = FormWindowState.Maximized;
            }
            _formLeft = Left;
            _formTop = Top;
            _formHeight = Height;
            _formWidth = Width;

            travelHistoryControl1.LoadLayoutSettings();
            journalViewControl1.LoadLayoutSettings();
            if (EDDConfig.AutoLoadPopOuts && EDDConfig.Options.NoWindowReposition == false)
                PopOuts.LoadSavedPopouts();
        }

        private void CheckIfEliteDangerousIsRunning()
        {
            if (EliteDangerousClass.EDRunning)
            {
                LogLine("EliteDangerous is running.");
            }
            else
            {
                LogLine("EliteDangerous is not running.");
            }
        }

        private void EDDiscoveryForm_Activated(object sender, EventArgs e)
        {
        }

        public void ApplyTheme()
        {
            ToolStripManager.Renderer = theme.toolstripRenderer;
            panel_close.Visible = !theme.WindowsFrame;
            panel_minimize.Visible = !theme.WindowsFrame;
            label_version.Visible = !theme.WindowsFrame;

            this.Text = "EDDiscovery " + label_version.Text;            // note in no border mode, this is not visible on the title bar but it is in the taskbar..

            theme.ApplyToForm(this);

            if (OnHistoryChange != null)
                OnHistoryChange(history);
        }

        #endregion

        #region Initial Check Systems

        private void _checkSystemsWorker_DoWork(object sender, System.ComponentModel.DoWorkEventArgs e)
        {
            try
            {
                var worker = (System.ComponentModel.BackgroundWorker)sender;

                CheckSystems(() => worker.CancellationPending, (p, s) => worker.ReportProgress(p, s));

                if (worker.CancellationPending)
                    e.Cancel = true;
            }
            catch (Exception ex) { e.Result = ex; }       // any exceptions, ignore
            finally
            {
                _checkSystemsWorkerCompletedEvent.Set();
            }
        }

        private void CheckSystems(Func<bool> cancelRequested, Action<int, string> reportProgress)  // ASYNC process, done via start up, must not be too slow.
        {
            reportProgress(-1, "");

            string rwsystime = SQLiteConnectionSystem.GetSettingString("EDSMLastSystems", "2000-01-01 00:00:00"); // Latest time from RW file.
            DateTime edsmdate;

            if (!DateTime.TryParse(rwsystime, CultureInfo.InvariantCulture, DateTimeStyles.None, out edsmdate))
            {
                edsmdate = new DateTime(2000, 1, 1);
            }

            if (DateTime.Now.Subtract(edsmdate).TotalDays > 7)  // Over 7 days do a sync from EDSM
            {
                // Also update galactic mapping from EDSM 
                LogLine("Get galactic mapping from EDSM.");
                galacticMapping.DownloadFromEDSM();

                // Skip EDSM full update if update has been performed in last 4 days
                bool outoforder = SQLiteConnectionSystem.GetSettingBool("EDSMSystemsOutOfOrder", true);
                DateTime lastmod = outoforder ? SystemClass.GetLastSystemModifiedTime() : SystemClass.GetLastSystemModifiedTimeFast();

                if (DateTime.UtcNow.Subtract(lastmod).TotalDays > 4 ||
                    DateTime.UtcNow.Subtract(edsmdate).TotalDays > 28)
                {
                    syncstate.performedsmsync = true;
                }
                else
                {
                    SQLiteConnectionSystem.PutSettingString("EDSMLastSystems", DateTime.Now.ToString(CultureInfo.InvariantCulture));
                }
            }

            if (!cancelRequested())
            {
                SQLiteConnectionUser.TranferVisitedSystemstoJournalTableIfRequired();
                SQLiteConnectionSystem.CreateSystemsTableIndexes();
                SystemNoteClass.GetAllSystemNotes();                                // fill up memory with notes, bookmarks, galactic mapping
                BookmarkClass.GetAllBookmarks();
                galacticMapping.ParseData();                            // at this point, EDSM data is loaded..
                SystemClass.AddToAutoComplete(galacticMapping.GetGMONames());
                EDDiscovery2.DB.MaterialCommodities.SetUpInitialTable();

                LogLine("Loaded Notes, Bookmarks and Galactic mapping.");

                string timestr = SQLiteConnectionSystem.GetSettingString("EDDBSystemsTime", "0");
                DateTime time = new DateTime(Convert.ToInt64(timestr), DateTimeKind.Utc);
                if (DateTime.UtcNow.Subtract(time).TotalDays > 6.5)     // Get EDDB data once every week.
                    syncstate.performeddbsync = true;
            }
        }

        private void _checkSystemsWorker_RunWorkerCompleted(object sender, System.ComponentModel.RunWorkerCompletedEventArgs e)
        {
            Exception ex = e.Cancelled ? null : (e.Error ?? e.Result as Exception);
            ReportProgress(-1, "");
            if (!e.Cancelled && !PendingClose)
            {
                if (ex != null)
                {
                    LogLineHighlight("Check Systems exception: " + ex.Message + Environment.NewLine + "Trace: " + ex.StackTrace);
                }

                imageHandler1.StartWatcher();
                routeControl1.EnableRouteTab(); // now we have systems, we can update this..

                routeControl1.travelhistorycontrol1 = travelHistoryControl1;
                journalmonitor.OnNewJournalEntry += NewPosition;
                EdsmSync.OnDownloadedSystems += RefreshDueToEDSMDownloadedSystems;


                LogLine("Reading travel history");
                HistoryRefreshed += _travelHistoryControl1_InitialRefreshDone;

                RefreshHistoryAsync();

                ShowInfoPanel("", false);

                checkInstallerTask = CheckForNewInstallerAsync();

                if (EDDN.EDDNClass.CheckforEDMC()) // EDMC is running
                {
                    if (EDDiscoveryForm.EDDConfig.CurrentCommander.SyncToEddn)  // Both EDD and EDMC should not sync to EDDN.
                    {
                        LogLineHighlight("EDDiscovery and EDMarketConnector should not both sync to EDDN. Stop EDMC or uncheck 'send to EDDN' in settings tab!");
                    }
                }
            }
        }

        private void RefreshDueToEDSMDownloadedSystems()
        {
            Invoke((MethodInvoker)delegate
            {
                RefreshHistoryAsync();
            });
        }


        private void _travelHistoryControl1_InitialRefreshDone(object sender, EventArgs e)
        {
            HistoryRefreshed -= _travelHistoryControl1_InitialRefreshDone;

            if (!PendingClose)
            {
                AsyncPerformSync();                              // perform any async synchronisations

                if (syncstate.performeddbsync || syncstate.performedsmsync)
                {
                    string databases = (syncstate.performedsmsync && syncstate.performeddbsync) ? "EDSM and EDDB" : ((syncstate.performedsmsync) ? "EDSM" : "EDDB");

                    LogLine("ED Discovery will now synchronise to the " + databases + " databases to obtain star information." + Environment.NewLine +
                                    "This will take a while, up to 15 minutes, please be patient." + Environment.NewLine +
                                    "Please continue running ED Discovery until refresh is complete.");
                }
            }
        }


        private void _checkSystemsWorker_ProgressChanged(object sender, System.ComponentModel.ProgressChangedEventArgs e)
        {
            ReportProgress(e.ProgressPercentage, (string)e.UserState);
        }

        #endregion

        #region Async EDSM/EDDB Full Sync

        private void AsyncPerformSync()
        {
            if (!_syncWorker.IsBusy)
            {
                edsmRefreshTimer.Enabled = false;
                _syncWorker.RunWorkerAsync();
            }
        }

        private void _syncWorker_DoWork(object sender, System.ComponentModel.DoWorkEventArgs e)
        {
            try
            {
                var worker = (System.ComponentModel.BackgroundWorker)sender;

                SystemClass.PerformSync(() => worker.CancellationPending, (p, s) => worker.ReportProgress(p, s), LogLine, LogLineHighlight, syncstate);
                if (worker.CancellationPending)
                    e.Cancel = true;
            }
            catch (Exception ex) { e.Result = ex; }       // ignore any excepctions
            finally
            {
                _syncWorkerCompletedEvent.Set();
            }
        }

        private SystemClass.SystemsSyncState syncstate = new SystemClass.SystemsSyncState();

        private void _syncWorker_RunWorkerCompleted(object sender, System.ComponentModel.RunWorkerCompletedEventArgs e)
        {
            Exception ex = e.Cancelled ? null : (e.Error ?? e.Result as Exception);
            ReportProgress(-1, "");

            if (!e.Cancelled && !PendingClose)
            {
                if (ex != null)
                {
                    LogLineHighlight("Check Systems exception: " + ex.Message + Environment.NewLine + "Trace: " + ex.StackTrace);
                }

                long totalsystems = SystemClass.GetTotalSystems();
                LogLineSuccess("Loading completed, total of " + totalsystems + " systems");

                if (syncstate.performhistoryrefresh)
                {
                    LogLine("Refresh due to updating systems");
                    HistoryRefreshed += HistoryFinishedRefreshing;
                    RefreshHistoryAsync();
                }

                edsmRefreshTimer.Enabled = true;
            }
        }

        private void HistoryFinishedRefreshing(object sender, EventArgs e)
        {
            HistoryRefreshed -= HistoryFinishedRefreshing;
            LogLine("Refreshing complete.");

            if (syncstate.syncwasfirstrun)
            {
                LogLine("EDSM and EDDB update complete. Please restart ED Discovery to complete the synchronisation ");
            }
            else if (syncstate.syncwaseddboredsm)
                LogLine("EDSM and/or EDDB update complete.");
        }

        private void _syncWorker_ProgressChanged(object sender, System.ComponentModel.ProgressChangedEventArgs e)
        {
            ReportProgress(e.ProgressPercentage, (string)e.UserState);
        }

        #endregion

        #region EDSM and EDDB syncs code

        private void edsmRefreshTimer_Tick(object sender, EventArgs e)
        {
            AsyncPerformSync();
        }

        #endregion

        #region Logging

        private string logtext = "";     // to keep in case of no logs..

        public string LogText { get { return logtext; } }

        public void LogLine(string text)
        {
            LogLineColor(text, theme.TextBlockColor);
        }

        public void LogLineHighlight(string text)
        {
            LogLineColor(text, theme.TextBlockHighlightColor);
        }

        public void LogLineSuccess(string text)
        {
            LogLineColor(text, theme.TextBlockSuccessColor);
        }

        public void LogLineColor(string text, Color color)
        {
            try
            {
                Invoke((MethodInvoker)delegate
                {
                    logtext += text + Environment.NewLine;      // keep this, may be the only log showing

                    if (OnNewLogEntry != null)
                        OnNewLogEntry(text + Environment.NewLine, color);
                });
            }
            catch { }
        }

        public void ReportProgress(int percentComplete, string message)
        {
            if (!PendingClose)
            {
                if (percentComplete >= 0)
                {
                    this.toolStripProgressBar1.Visible = true;
                    this.toolStripProgressBar1.Value = percentComplete;
                }
                else
                {
                    this.toolStripProgressBar1.Visible = false;
                }

                this.toolStripStatusLabel1.Text = message;
            }
        }


        #endregion

        #region JSONandMisc
        static public string LoadJsonFile(string filename)
        {
            string json = null;
            try
            {
                if (!File.Exists(filename))
                    return null;

                StreamReader reader = new StreamReader(filename);
                json = reader.ReadToEnd();
                reader.Close();
            }
            catch
            {
            }

            return json;
        }

        internal void ShowTrilaterationTab()
        {
            tabControl1.SelectedIndex = 1;
        }

        #endregion

        #region Closing

        private void SaveSettings()
        {
            settings.SaveSettings();

            SQLiteDBClass.PutSettingBool("FormMax", _formMax);
            SQLiteDBClass.PutSettingInt("FormWidth", _formWidth);
            SQLiteDBClass.PutSettingInt("FormHeight", _formHeight);
            SQLiteDBClass.PutSettingInt("FormTop", _formTop);
            SQLiteDBClass.PutSettingInt("FormLeft", _formLeft);
            routeControl1.SaveSettings();
            theme.SaveSettings(null);
            travelHistoryControl1.SaveSettings();
            journalViewControl1.SaveSettings();
            if (EDDConfig.AutoSavePopOuts)
                PopOuts.SaveCurrentPopouts();
        }

        Thread safeClose;
        System.Windows.Forms.Timer closeTimer;

        public bool PendingClose { get { return safeClose != null; } }           // we want to close boys!

        public void ShowInfoPanel(string message, bool visible, Color? backColour = null)
        {
            labelPanelText.Text = message;
            panelInfo.Visible = visible;
            if (backColour.HasValue) panelInfo.BackColor = backColour.Value;
        }

        private void EDDiscoveryForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (safeClose == null)                  // so a close is a request now, and it launches a thread which cleans up the system..
            {
                e.Cancel = true;
                edsmRefreshTimer.Enabled = false;
                CancellationTokenSource.Cancel();
                CancelHistoryRefresh();
                EDDNSync.StopSync();
                _syncWorker.CancelAsync();
                _checkSystemsWorker.CancelAsync();
                if (cancelDownloadMaps != null)
                {
                    cancelDownloadMaps();
                }
                ShowInfoPanel("Closing, please wait!", true);
                LogLineHighlight("Closing down, please wait..");
                Console.WriteLine("Close.. safe close launched");
                safeClose = new Thread(SafeClose) { Name = "Close Down", IsBackground = true };
                safeClose.Start();
                Actions.ActionSay.KillSpeech();
            }
            else if (safeClose.IsAlive)   // still working, cancel again..
            {
                e.Cancel = true;
            }
            else
            {
                Console.WriteLine("go for close");
            }
        }

        private void SafeClose()        // ASYNC thread..
        {
            Thread.Sleep(1000);
            Console.WriteLine("Waiting for check systems to close");
            if (_checkSystemsWorker.IsBusy)
                _checkSystemsWorkerCompletedEvent.WaitOne();

            Console.WriteLine("Waiting for full sync to close");
            if (_syncWorker.IsBusy)
                _syncWorkerCompletedEvent.WaitOne();

            Console.WriteLine("Stopping discrete threads");
            journalmonitor.StopMonitor();

            if (EdsmSync != null)
                EdsmSync.StopSync();

            travelHistoryControl1.CloseClosestSystemThread();

            Console.WriteLine("Go for close timer!");

            Invoke((MethodInvoker)delegate          // we need this thread to die so close will work, so kick off a timer
            {
                closeTimer = new System.Windows.Forms.Timer();
                closeTimer.Interval = 100;
                closeTimer.Tick += new EventHandler(CloseItFinally);
                closeTimer.Start();
            });
        }

        void CloseItFinally(Object sender, EventArgs e)
        {
            if (safeClose.IsAlive)      // still alive, try again
                closeTimer.Start();
            else
            {
                closeTimer.Stop();      // stop timer now. So it won't try to save it multiple times during close down if it takes a while - this caused a bug in saving some settings
                SaveSettings();         // do close now
                notifyIcon1.Visible = false;
                Close();
                Application.Exit();
            }
        }

        #endregion

#region Buttons, Mouse, Menus, NotifyIcon

        private void button_test_Click(object sender, EventArgs e)
        {
            ActionRunOnEvent("onStartup", "ProgramEvent");
        }

        private void addNewStarToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Process.Start("http://robert.astronet.se/Elite/ed-systems/entry.html");
        }

        private void frontierForumThreadToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Process.Start(Properties.Resources.URLProjectEDForumPost);
        }

        private void eDDiscoveryFGESupportThreadToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Process.Start("http://firstgreatexpedition.org/mybb/showthread.php?tid=1406");
        }

        private void eDDiscoveryHomepageToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Process.Start(Properties.Resources.URLProjectWiki);
        }

        private void openEliteDangerousDirectoryToolStripMenuItem_Click(object sender, EventArgs e)
        {
            try
            {
                if (EliteDangerousClass.EDDirectory != null && !EliteDangerousClass.EDDirectory.Equals(""))
                    Process.Start(EliteDangerousClass.EDDirectory);

            }
            catch (Exception ex)
            {
                MessageBox.Show("Open EliteDangerous directory exception: " + ex.Message);
            }

        }

        private void showLogfilesToolStripMenuItem_Click(object sender, EventArgs e)
        {
            try
            {
                EDCommander cmdr = EDDConfig.Instance.ListOfCommanders.Find(x => x.Nr == EDDConfig.Instance.CurrentCmdrID);

                if (cmdr != null)
                {
                    string cmdrfolder = cmdr.JournalDir;
                    if (cmdrfolder.Length < 1)
                        cmdrfolder = EliteDangerous.EDJournalClass.GetDefaultJournalDir();
                    Process.Start(cmdrfolder);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Show log files exception: " + ex.Message);
            }
        }

        private void show2DMapsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Open2DMap();
        }

        private void show3DMapsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            TravelControl.buttonMap_Click(sender, e);
        }

        private void forceEDDBUpdateToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (!_syncWorker.IsBusy)      // we want it to have run, to completion, to allow another go..
            {
                syncstate.performeddbsync = true;
                AsyncPerformSync();
            }
            else
                MessageBox.Show("Synchronisation to databases is in operation or pending, please wait");
        }

        private void syncEDSMSystemsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (!_syncWorker.IsBusy)      // we want it to have run, to completion, to allow another go..
            {
                syncstate.performedsmsync = true;
                AsyncPerformSync();
            }
            else
                MessageBox.Show("Synchronisation to databases is in operation or pending, please wait");
        }

        private void gitHubToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Process.Start(Properties.Resources.URLProjectGithub);
        }

        private void reportIssueIdeasToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Process.Start(Properties.Resources.URLProjectFeedback);
        }

        internal void keepOnTopChanged(bool keepOnTop)
        {
            this.TopMost = keepOnTop;
        }

        /// <summary>
        /// The settings panel check box for 'Use notification area icon' has changed.
        /// </summary>
        /// <param name="useNotifyIcon">Whether or not the setting is enabled.</param>
        internal void useNotifyIconChanged(bool useNotifyIcon)
        {
            notifyIcon1.Visible = useNotifyIcon;
            if (!useNotifyIcon && !Visible)
                Show();
        }

        private void panel_minimize_Click(object sender, EventArgs e)
        {
            this.WindowState = FormWindowState.Minimized;
        }

        private void changeMapColorToolStripMenuItem_Click(object sender, EventArgs e)
        {
            settings.panel_defaultmapcolor_Click(sender, e);
        }

        private void editThemeToolStripMenuItem_Click(object sender, EventArgs e)
        {
            settings.button_edittheme_Click(this, null);
        }

        private void exitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Close();
        }

        private void AboutBox()
        {
            AboutForm frm = new AboutForm();
            frm.labelVersion.Text = this.Text;
            frm.TopMost = EDDiscoveryForm.EDDConfig.KeepOnTop;
            frm.ShowDialog(this);
        }

        private void aboutToolStripMenuItem_Click(object sender, EventArgs e)
        {
            AboutBox();
        }

        private void eDDiscoveryChatDiscordToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Process.Start(Properties.Resources.URLProjectDiscord);
        }

        private void showAllInTaskBarToolStripMenuItem_Click(object sender, EventArgs e)
        {
            PopOuts.ShowAllPopOutsInTaskBar();
        }

        private void turnOffAllTransparencyToolStripMenuItem_Click(object sender, EventArgs e)
        {
            PopOuts.MakeAllPopoutsOpaque();
        }

        private void clearEDSMIDAssignedToAllRecordsForCurrentCommanderToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (MessageBox.Show("Confirm you wish to reset the assigned EDSM IDs to all the current commander history entries," +
                                " and clear all the assigned EDSM IDs in all your notes for all commanders\r\n\r\n" +
                                "This will not change your history, but when you next refresh, it will try and reassign EDSM systems to " +
                                "your history and notes.  Use only if you think that the assignment of EDSM systems to entries is grossly wrong," +
                                "or notes are going missing\r\n" +
                                "\r\n" +
                                "You can manually change one EDSM assigned system by right clicking on the travel history and selecting the option"
                                , "WARNING", MessageBoxButtons.OKCancel) == DialogResult.OK)
            {
                EliteDangerous.JournalEntry.ClearEDSMID(EDDConfig.CurrentCommander.Nr);
                SystemNoteClass.ClearEDSMID();
            }

        }


        private void paneleddiscovery_Click(object sender, EventArgs e)
        {
            AboutBox();
        }

        private void panel_close_Click(object sender, EventArgs e)
        {
            Close();
        }

        private void read21AndFormerLogFilesToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Read21Folders(false);
        }

        private void read21AndFormerLogFiles_forceReloadLogsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Read21Folders(true);
        }

        private void Read21Folders(bool force)
        { 
            if (history.CommanderId >= 0)
            {
                EDCommander cmdr = EDDConfig.ListOfCommanders.Find(c => c.Nr == history.CommanderId);
                if (cmdr != null)
                {
                    string netlogpath = cmdr.NetLogDir;
                    FolderBrowserDialog dirdlg = new FolderBrowserDialog();
                    if (netlogpath != null && Directory.Exists(netlogpath))
                    {
                        dirdlg.SelectedPath = netlogpath;
                    }

                    DialogResult dlgResult = dirdlg.ShowDialog();

                    if (dlgResult == DialogResult.OK)
                    {
                        string logpath = dirdlg.SelectedPath;

                        if (logpath != netlogpath)
                        {
                            cmdr.NetLogDir = logpath;
                            EDDConfig.UpdateCommanders(new List<EDCommander> { cmdr }, true);
                        }

                        //string logpath = "c:\\games\\edlaunch\\products\\elite-dangerous-64\\logs";
                        RefreshHistoryAsync(netlogpath: logpath, forcenetlogreload: force, currentcmdr: cmdr.Nr);
                    }
                }
            }
        }

        private void dEBUGResetAllHistoryToFirstCommandeToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (MessageBox.Show("Confirm you wish to reset all history entries to the current commander", "WARNING", MessageBoxButtons.OKCancel) == DialogResult.OK)
            {
                EliteDangerous.JournalEntry.ResetCommanderID(-1, EDDConfig.CurrentCommander.Nr);
                RefreshHistoryAsync();
            }
        }


        private void rescanAllJournalFilesToolStripMenuItem_Click(object sender, EventArgs e)
        {
            RefreshHistoryAsync(forcejournalreload: true, checkedsm: true);
        }

        private void checkForNewReleaseToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (CheckForNewinstaller())
            {
                if (newRelease != null)
                {
                    NewReleaseForm frm = new NewReleaseForm();
                    frm.release = newRelease;

                    frm.ShowDialog(this);
                }
            }
            else
            {
                MessageBox.Show("No new release found", "EDDiscovery", MessageBoxButtons.OK);
            }
        }

        private void deleteDuplicateFSDJumpEntriesToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (MessageBox.Show("Confirm you remove any duplicate FSD entries from the current commander", "WARNING", MessageBoxButtons.OKCancel) == DialogResult.OK)
            {
                int n = EliteDangerous.JournalEntry.RemoveDuplicateFSDEntries(EDDConfig.CurrentCommander.Nr);
                LogLine("Removed " + n + " FSD entries");
                RefreshHistoryAsync();
            }
        }

        private void panelInfo_Click(object sender, EventArgs e)
        {
            if (newRelease != null)
            {
                NewReleaseForm frm = new NewReleaseForm();
                frm.release = newRelease;

                frm.ShowDialog(this);
            }
        }

        private void labelPanelText_Click(object sender, EventArgs e)
        {
            if (newRelease != null)
            {
                NewReleaseForm frm = new NewReleaseForm();
                frm.release = newRelease;

                frm.ShowDialog(this);
            }
        }

        public void Open3DMap(HistoryEntry he)
        {
            this.Cursor = Cursors.WaitCursor;

            string HomeSystem = settings.MapHomeSystem;

            history.FillInPositionsFSDJumps();

            Map.Prepare(he?.System, HomeSystem,
                        settings.MapCentreOnSelection ? he?.System : SystemClass.GetSystem(String.IsNullOrEmpty(HomeSystem) ? "Sol" : HomeSystem),
                        settings.MapZoom, history.FilterByTravel);
            Map.Show();
            this.Cursor = Cursors.Default;
        }

        public void Open2DMap()
        {
            this.Cursor = Cursors.WaitCursor;
            FormSagCarinaMission frm = new FormSagCarinaMission(history.FilterByFSDAndPosition);
            frm.Nowindowreposition = EDDConfig.Options.NoWindowReposition;
            frm.Show();
            this.Cursor = Cursors.Default;
        }

        private void sendUnsuncedEDDNEventsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            List<HistoryEntry> hlsyncunsyncedlist = history.FilterByScanNotEDDNSynced;        // first entry is oldest
            EDDNSync.SendEDDNEvents(this, hlsyncunsyncedlist);
        }

        private void materialSearchToolStripMenuItem_Click(object sender, EventArgs e)
        {
            FindMaterialsForm frm = new FindMaterialsForm();

            frm.Show(this);
        }

        private void notifyIcon1_DoubleClick(object sender, EventArgs e)
        {
            // Tray icon was double-clicked.
            if (FormWindowState.Minimized == WindowState)
            {
                if (EDDConfig.MinimizeToNotifyIcon)
                    Show();
                if (_formMax)
                    WindowState = FormWindowState.Maximized;
                else
                    WindowState = FormWindowState.Normal;
            }
            else
                WindowState = FormWindowState.Minimized;
        }

        private void notifyIconMenu_Hide_Click(object sender, EventArgs e)
        {
            // Tray icon 'Hide Tray Icon' menu item was clicked.
            settings.checkBoxUseNotifyIcon.Checked = false;
        }

        private void notifyIconMenu_Open_Click(object sender, EventArgs e)
        {
            // Tray icon 'Open EDDiscovery' menu item was clicked. Present the main window.
            if (FormWindowState.Minimized == WindowState)
            {
                if (EDDConfig.UseNotifyIcon && EDDConfig.MinimizeToNotifyIcon)
                    Show();
                if (_formMax)
                    WindowState = FormWindowState.Maximized;
                else
                    WindowState = FormWindowState.Normal;
            }
            else
                Activate();
        }

        #endregion

        #region Window Control

        protected override void WndProc(ref Message m)
        {
            // Compatibility movement for Mono
            if (m.Msg == WM_LBUTTONDOWN && (int)m.WParam == 1 && !theme.WindowsFrame)
            {
                int x = unchecked((short)((uint)m.LParam & 0xFFFF));
                int y = unchecked((short)((uint)m.LParam >> 16));
                _window_dragMousePos = new Point(x, y);
                _window_dragWindowPos = this.Location;
                _window_dragging = true;
                m.Result = IntPtr.Zero;
                this.Capture = true;
            }
            else if (m.Msg == WM_MOUSEMOVE && (int)m.WParam == 1 && _window_dragging)
            {
                int x = unchecked((short)((uint)m.LParam & 0xFFFF));
                int y = unchecked((short)((uint)m.LParam >> 16));
                Point delta = new Point(x - _window_dragMousePos.X, y - _window_dragMousePos.Y);
                _window_dragWindowPos = new Point(_window_dragWindowPos.X + delta.X, _window_dragWindowPos.Y + delta.Y);
                this.Location = _window_dragWindowPos;
                this.Update();
                m.Result = IntPtr.Zero;
            }
            else if (m.Msg == WM_LBUTTONUP)
            {
                _window_dragging = false;
                _window_dragMousePos = Point.Empty;
                _window_dragWindowPos = Point.Empty;
                m.Result = IntPtr.Zero;
                this.Capture = false;
            }
            // Windows honours NCHITTEST; Mono does not
            else if (m.Msg == WM_NCHITTEST)
            {
                base.WndProc(ref m);
                //System.Diagnostics.Debug.WriteLine( Environment.TickCount + " Res " + ((int)m.Result));

                if ((int)m.Result == HT_CLIENT)
                {
                    int x = unchecked((short)((uint)m.LParam & 0xFFFF));
                    int y = unchecked((short)((uint)m.LParam >> 16));
                    Point p = PointToClient(new Point(x, y));

                    if (p.X > this.ClientSize.Width - statusStrip1.Height && p.Y > this.ClientSize.Height - statusStrip1.Height)
                    {
                        m.Result = (IntPtr)HT_BOTTOMRIGHT;
                    }
                    else if (p.Y > this.ClientSize.Height - statusStrip1.Height)
                    {
                        m.Result = (IntPtr)HT_BOTTOM;
                    }
                    else if (p.X > this.ClientSize.Width - 5)       // 5 is generous.. really only a few pixels gets thru before the subwindows grabs them
                    {
                        m.Result = (IntPtr)HT_RIGHT;
                    }
                    else if (p.X < 5)
                    {
                        m.Result = (IntPtr)HT_LEFT;
                    }
                    else if (!theme.WindowsFrame)
                    {
                        m.Result = (IntPtr)HT_CAPTION;
                    }
                }
            }
            else
            {
                base.WndProc(ref m);
            }
        }

        private void RecordPosition()
        {
            if (FormWindowState.Minimized != WindowState)
            {
                _formLeft = this.Left;
                _formTop = this.Top;
                _formWidth = this.Width;
                _formHeight = this.Height;
                _formMax = FormWindowState.Maximized == WindowState;
            }
        }

        private void EDDiscoveryForm_Resize(object sender, EventArgs e)
        {
            // We may be getting called by this.ResumeLayout() from InitializeComponent().
            if (EDDConfig != null)
            {
                if (EDDConfig.UseNotifyIcon && EDDConfig.MinimizeToNotifyIcon)
                {
                    if (FormWindowState.Minimized == WindowState)
                        Hide();
                    else
                        Show();
                }
                RecordPosition();
                notifyIconMenu_Open.Enabled = FormWindowState.Minimized == WindowState;
            }
        }

        private void EDDiscoveryForm_ResizeEnd(object sender, EventArgs e)
        {
            RecordPosition();
        }

        #endregion

        #region Update Data

        protected class RefreshWorkerArgs
        {
            public string NetLogPath;
            public bool ForceNetLogReload;
            public bool ForceJournalReload;
            public bool CheckEdsm;
            public int CurrentCommander;
        }

        protected class RefreshWorkerResults
        {
            public List<HistoryEntry> rethistory;
            public MaterialCommoditiesLedger retledger;
            public StarScan retstarscan;
        }

        public void RefreshHistoryAsync(string netlogpath = null, bool forcenetlogreload = false, bool forcejournalreload = false, bool checkedsm = false, int? currentcmdr = null)
        {
            if (PendingClose)
            {
                return;
            }

            if (!_refreshWorker.IsBusy)
            {
                travelHistoryControl1.RefreshButton(false);
                journalViewControl1.RefreshButton(false);

                journalmonitor.StopMonitor();          // this is called by the foreground.  Ensure background is stopped.  Foreground must restart it.

                RefreshWorkerArgs args = new RefreshWorkerArgs
                {
                    NetLogPath = netlogpath,
                    ForceNetLogReload = forcenetlogreload,
                    ForceJournalReload = forcejournalreload,
                    CheckEdsm = checkedsm,
                    CurrentCommander = currentcmdr ?? history.CommanderId
                };

                ActionRunOnEvent("onRefreshStart", "ProgramEvent");

                _refreshWorker.RunWorkerAsync(args);
            }
        }

        public void CancelHistoryRefresh()
        {
            _refreshWorker.CancelAsync();
        }

        private void RefreshHistoryWorker(object sender, DoWorkEventArgs e)
        {
            RefreshWorkerArgs args = e.Argument as RefreshWorkerArgs;
            var worker = (BackgroundWorker)sender;

            HistoryList hist = HistoryList.LoadHistory(journalmonitor, () => worker.CancellationPending, (p, s) => worker.ReportProgress(p, s), args.NetLogPath, args.ForceJournalReload, args.ForceJournalReload, args.CheckEdsm, args.CurrentCommander);

            if (worker.CancellationPending)
            {
                e.Cancel = true;
            }
            else
            {
                e.Result = hist;
            }
        }

        private void RefreshHistoryWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            if (!e.Cancelled && !PendingClose)
            {
                if (e.Error != null)
                {
                    LogLineHighlight("History Refresh Error: " + e.Error.Message);
                }
                else
                {
                    string prevcommander = globalvariables.ContainsKey("Commander") ? globalvariables["Commander"] : "None";
                    string commander = (history.CommanderId < 0) ? "Hidden" : EDDConfig.Instance.CurrentCommander.Name;

                    string refreshcount = prevcommander.Equals(commander) ? internalglobalvariables.AddToVar("RefreshCount", 1, 1) : "1";
                    SetInternalGlobal("RefreshCount", refreshcount);
                    SetInternalGlobal("Commander", commander);

                    travelHistoryControl1.LoadCommandersListBox();             // in case a new commander has been detected
                    exportControl1.PopulateCommanders();
                    settings.UpdateCommandersListBox();

                    history.Clear();

                    HistoryList hist = (HistoryList)e.Result;

                    foreach (var ent in hist.EntryOrder)
                    {
                        history.Add(ent);
                        Debug.Assert(ent.MaterialCommodity != null);
                    }

                    history.materialcommodititiesledger = hist.materialcommodititiesledger;
                    history.starscan = hist.starscan;
                    history.CommanderId = hist.CommanderId;

                    ReportProgress(-1, "");
                    LogLine("Refresh Complete.");

                    if (OnHistoryChange != null)
                        OnHistoryChange(history);
                }

                travelHistoryControl1.RefreshButton(true);
                journalViewControl1.RefreshButton(true);

                if (HistoryRefreshed != null)
                    HistoryRefreshed(this, EventArgs.Empty);

                journalmonitor.StartMonitor();

                ActionRunOnEvent("onRefreshEnd", "ProgramEvent");
            }
        }

        private void RefreshHistoryWorkerProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            string name = (string)e.UserState;
            ReportProgress(e.ProgressPercentage, $"Processing log file {name}");
        }

        public void RefreshDisplays()
        {
            if (OnHistoryChange != null)
                OnHistoryChange(history);
        }

        public void NewPosition(EliteDangerous.JournalEntry je)
        {
            Debug.Assert(Application.MessageLoop);              // ensure.. paranoia

            if (je.CommanderId == history.CommanderId)     // we are only interested at this point accepting ones for the display commander
            {
                HistoryEntry he = history.AddJournalEntry(je, h => LogLineHighlight(h));

                if (he != null && OnNewEntry != null)
                    OnNewEntry(he, history);

                ActionRunOnEntry(he, "NewEntry");
            }

            if (OnNewJournalEntry != null)
            {
                OnNewJournalEntry(je);
            }

            travelHistoryControl1.LoadCommandersListBox();  // because we may have new commanders
            settings.UpdateCommandersListBox();
            exportControl1.PopulateCommanders();
        }

        public void RecalculateHistoryDBs()         // call when you need to recalc the history dbs - not the whole history. Use RefreshAsync for that
        {
            history.ProcessUserHistoryListEntries(h => h.EntryOrder);

            if (OnHistoryChange != null)
                OnHistoryChange(history);
        }


        #endregion

        #region Targets

        public void NewTargetSet()
        {
            System.Diagnostics.Debug.WriteLine("New target set");
            if (OnNewTarget != null)
                OnNewTarget();
        }

        #endregion

        #region Actions

        public void StartUpActions()
        {
            actionfiles = new Actions.ActionFileList();
            actionfiles.LoadAllActionFiles();
            actionrunasync = new Actions.ActionRun(this, actionfiles, true);        // this is the guy who runs programs asynchronously

            ActionConfigureKeys();
        }

        public void ConfigureActions()
        {
            EDDiscovery2.ConditionFilterForm frm = new ConditionFilterForm();

            List<string> events = EDDiscovery.EliteDangerous.JournalEntry.GetListOfEventsWithOptMethod(false);
            events.Add("All");
            events.Add("onRefreshStart");
            events.Add("onRefreshEnd");
            events.Add("onStartup");
            events.Add("onKeyPress");
            //events.Add("onClosedown");

            frm.InitAction("Actions: Define actions", events, globalvariables.KeyList, usercontrolledglobalvariables, actionfiles, theme);
            frm.TopMost = this.FindForm().TopMost;

            frm.ShowDialog(this.FindForm()); // don't care about the result, the form does all the saving

            usercontrolledglobalvariables = frm.userglobalvariables;
            SQLiteConnectionUser.PutSettingString("UserGlobalActionVars", usercontrolledglobalvariables.ToString());

            globalvariables = new ConditionVariables(internalglobalvariables, usercontrolledglobalvariables);    // remake

            ActionConfigureKeys();
        }

        public void ActionRunOnEntry(HistoryEntry he , string triggertype , string flagstart = null )       //set flagstart to be the first flag of the actiondata..
        {
            List<Actions.ActionFileList.MatchingSets> ale = actionfiles.GetMatchingConditions(he.journalEntry.EventTypeStr , flagstart);

            if ( ale.Count > 0 )
            {
                ConditionVariables testvars = new ConditionVariables(globalvariables);
                Actions.ActionVars.TriggerVars(testvars, he.journalEntry.EventTypeStr, triggertype);
                Actions.ActionVars.HistoryEventVars(testvars, he, "Event");

                ConditionFunctions functions = new ConditionFunctions(this, history, he);                   // function handler

                if ( actionfiles.CheckActions(ale, he.journalEntry.EventDataString, testvars, functions.ExpandString) > 0 )
                {
                    ConditionVariables eventvars = new ConditionVariables();        // we don't pass globals in - added when they are run
                    Actions.ActionVars.TriggerVars(eventvars, he.journalEntry.EventTypeStr, triggertype);
                    Actions.ActionVars.HistoryEventVars(eventvars, he, "Event");
                    eventvars.GetJSONFieldNamesAndValues(he.journalEntry.EventDataString, "EventJS_");        // for all events, add to field list

                    actionfiles.RunActions(ale, actionrunasync, eventvars, history, he);  // add programs to action run

                    actionrunasync.Execute();       // will execute
                }
            }
        }

        public void ActionRunOnEvent( string name, string triggertype )
        {
            List<Actions.ActionFileList.MatchingSets> ale = actionfiles.GetMatchingConditions(name);

            if ( ale.Count > 0 )
            {
                ConditionVariables testvars = new ConditionVariables(globalvariables);
                Actions.ActionVars.TriggerVars(testvars, name, triggertype);

                ConditionFunctions functions = new ConditionFunctions(this, history, null);                   // function handler

                if ( actionfiles.CheckActions(ale, null, testvars, functions.ExpandString) > 0 )
                {
                    ConditionVariables eventvars = new ConditionVariables();
                    Actions.ActionVars.TriggerVars(eventvars, name, triggertype);

                    actionfiles.RunActions(ale, actionrunasync, eventvars, history, null);  // add programs to action run

                    actionrunasync.Execute();       // will execute
                }
            }
        }

        private void SetInternalGlobal(string name, string value)
        {
            internalglobalvariables[name] = globalvariables[name] = value;
        }

        public void SetProgramGlobal(string name, string value)     // different name for identification purposes
        {
            internalglobalvariables[name] = globalvariables[name] = value;
        }

        void ActionConfigureKeys()
        {
            List<Tuple<string, ConditionLists.MatchType>> ret = actionfiles.ReturnValuesOfSpecificConditions("KeyPress", new List<ConditionLists.MatchType>() { ConditionLists.MatchType.Equals, ConditionLists.MatchType.IsOneOf });        // need these to decide

            if (ret.Count > 0)
            {
                actionfileskeyevents = "";
                foreach (Tuple<string, ConditionLists.MatchType> t in ret)                  // go thru the list, making up a comparision string with Name, on it..
                {
                    if (t.Item2 == ConditionLists.MatchType.Equals)
                        actionfileskeyevents += "<" + t.Item1 + ">";
                    else
                    {
                        StringParser p = new StringParser(t.Item1);
                        List<string> klist = p.NextQuotedWordList();
                        if (klist != null)
                        {
                            foreach( string s in klist )
                                actionfileskeyevents += "<" + s + ">";
                        }
                    }
                }

                if (actionfilesmessagefilter == null)
                {
                    actionfilesmessagefilter = new ActionMessageFilter(this);
                    Application.AddMessageFilter(actionfilesmessagefilter);
                    System.Diagnostics.Debug.WriteLine("Installed message filter for keys");
                }
            }
            else if (actionfilesmessagefilter != null)
            {
                Application.RemoveMessageFilter(actionfilesmessagefilter);
                actionfilesmessagefilter = null;
                System.Diagnostics.Debug.WriteLine("Removed message filter for keys");
            }
        }

        public bool CheckKeys(string keyname)
        {
            if (actionfileskeyevents.Contains("<" + keyname + ">"))  // fast string comparision to determine if key is overridden..
            {
                globalvariables["KeyPress"] = keyname;          // only add it to global variables, its not kept in internals.
                ActionRunOnEvent("onKeyPress", "KeyPress");
                return true;
            }
            else
                return false;
        }

        const int WM_KEYDOWN = 0x100;
        const int WM_KEYCHAR = 0x102;
        const int WM_SYSKEYDOWN = 0x104;

        public class ActionMessageFilter : IMessageFilter
        {
            EDDiscoveryForm discoveryForm;
            public ActionMessageFilter(EDDiscoveryForm ed)
            {
                discoveryForm = ed;
            }

            public bool PreFilterMessage(ref Message m)
            {
                if (m.Msg == WM_KEYDOWN || m.Msg == WM_SYSKEYDOWN)
                {
                    Keys k = (Keys)m.WParam;
                    if (k != Keys.ControlKey && k != Keys.ShiftKey && k != Keys.Menu)
                    {
                        System.Diagnostics.Debug.WriteLine("Keydown " + m.LParam + " " + k.ToString(Control.ModifierKeys) + " " + m.WParam + " " + Control.ModifierKeys);
                        if (discoveryForm.CheckKeys(k.ToString(Control.ModifierKeys)))
                            return true;    // swallow, we did it
                    }
                }

                return false;
            }
        }


        #endregion
    }
}


