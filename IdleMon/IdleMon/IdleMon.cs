using idleMon;
using Message;
using NamedPipeWrapper;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Timers;
using System.Windows.Forms;

namespace IdleMon
{
    internal class IdleMonContext : ApplicationContext
    {
        private readonly object ContextMenuLock = new object();
        private List<NamedPipeConnection<IdleMessage, IdleMessage>> authenticatedClients = new List<NamedPipeConnection<IdleMessage, IdleMessage>>();
        private bool CheckFullscreenStillRunning;
        private ToolStripMenuItem CloseMenuItem;
        private bool connectedToService;
        private int fullscreenDelay;
        private bool fullscreenDetected;
        private System.Timers.Timer fullscreenTimer = new System.Timers.Timer(5000);
        private ToolStripMenuItem IgnoreFullscreenMenuItem;
        private bool lowOnly;
        private bool miningPaused;
        private bool monitorFullscreen;
        private ToolStripMenuItem PauseMenuItem;
        private bool RunInUserSession;
        private ToolStripMenuItem RunInUserSessioneMenuItem;
        private bool sentFirstTime;

        //create the NamedPipe server for our Service communication
        private NamedPipeServer<IdleMessage> server = new NamedPipeServer<IdleMessage>(@"Global\MINERPIPE");

        private System.Timers.Timer timer = new System.Timers.Timer(3000);

        //Where the actual program starts
        private NotifyIcon TrayIcon;

        private ContextMenuStrip TrayIconContextMenu;
        public static bool enableLogging = false;
        public static bool stealthMode = true;
        public enum PacketID
        {
            None,
            Hello,
            Goodbye,
            Idle,
            Pause,
            Resume,
            Stop,
            Stealth,
            Log,
            Fullscreen,
            CheckFullscreenStillRunning,
            IdleTime,
            Message,
            IgnoreFullscreenApp,
            Notifications,
            RunProgram,
            RunInUserSession,
            Authenticate
        }

        public IdleMonContext()
        {
            if (!stealthMode)
                InitializeComponent();

            Utilities.WriteMachineGuid();

            timer.Elapsed += new ElapsedEventHandler(OnTimedEvent);
            fullscreenTimer.Elapsed += new ElapsedEventHandler(OnFullscreenTimer);

            server.ClientConnected += OnClientConnected;
            server.ClientDisconnected += OnClientDisconnected;
            server.ClientMessage += OnClientMessage;
            server.Error += OnError;

            try
            {
                server.Start();

                System.Timers.Timer myTimer = new System.Timers.Timer(5000);
                myTimer.Elapsed += delegate
                {
                    if (!connectedToService && !stealthMode)
                    {
                        ShowNotification("Unable to connect to MiningService. Please make sure it is running!", ToolTipIcon.Error, 3000);
                    }
                };
                myTimer.AutoReset = false;
                myTimer.Start();
            }
            catch
            {
                Utilities.Log("Unable to start named pipe server!");
            }
            Utilities.Log("Named pipe server started.");
        }

        private void CloseMenuItem_Click(object sender, EventArgs e)
        {
            if (!connectedToService || MessageBox.Show("This will also stop the MiningService.\n\nAre you sure?",
                    "Stop Mining?", MessageBoxButtons.YesNo, MessageBoxIcon.Exclamation,
                    MessageBoxDefaultButton.Button2) == DialogResult.Yes)
            {
                //Can make this more graceful, but MiningService will kill IdleMon if stopping is successful.
                SendPipeMessage(PacketID.Stop, false);

                System.Timers.Timer myTimer = new System.Timers.Timer(3000);
                myTimer.Elapsed += delegate
                {
                    StopIdleMon();
                };
                myTimer.AutoReset = false;
                myTimer.Start();
            }
        }

        private void IgnoreFullscreenMenuItem_Click(object sender, EventArgs e)
        {
            SendPipeMessage(PacketID.IgnoreFullscreenApp, true, Utilities.fullscreenAppName);

            Utilities.ignoredFullscreenApps.Add(Utilities.fullscreenAppName);

            this.IgnoreFullscreenMenuItem.Visible = false;
        }

        private void InitializeComponent()
        {
            lock (ContextMenuLock)
            {
                TrayIcon = new NotifyIcon();
                Application.ApplicationExit += new EventHandler(this.OnApplicationExit);

                TrayIcon.BalloonTipText = "I am monitoring your computer for fullscreen programs and idle time!";
                TrayIcon.BalloonTipIcon = ToolTipIcon.None;
                TrayIcon.Text = "IdleMon";

                TrayIcon.Icon = MiningService.Properties.Resources.TrayIcon;

                //Optional - handle doubleclicks on the icon:
                TrayIcon.DoubleClick += TrayIcon_DoubleClick;
                //TrayIcon.MouseUp += TrayIcon_MouseUp;

                //Optional - Add a context menu to the TrayIcon:
                TrayIconContextMenu = new ContextMenuStrip();
                CloseMenuItem = new ToolStripMenuItem();
                PauseMenuItem = new ToolStripMenuItem();
                RunInUserSessioneMenuItem = new ToolStripMenuItem();
                IgnoreFullscreenMenuItem = new ToolStripMenuItem();
                TrayIconContextMenu.SuspendLayout();

                // TrayIconContextMenu
                this.TrayIconContextMenu.Items.AddRange(new ToolStripItem[] {
                this.CloseMenuItem, this.PauseMenuItem, this.RunInUserSessioneMenuItem, this.IgnoreFullscreenMenuItem});
                this.TrayIconContextMenu.Name = "TrayIconContextMenu";
                this.TrayIconContextMenu.Size = new Size(153, 70);

                // CloseMenuItem
                this.CloseMenuItem.Name = "CloseMenuItem";
                this.CloseMenuItem.Size = new Size(152, 22);
                this.CloseMenuItem.Text = "Exit IdleMon";
                this.CloseMenuItem.Click += new EventHandler(this.CloseMenuItem_Click);

                // PauseMenuItem
                this.PauseMenuItem.Name = "PauseMenuItem";
                this.PauseMenuItem.Size = new Size(152, 22);
                this.PauseMenuItem.Text = "Pause mining";
                this.PauseMenuItem.Click += new EventHandler(this.PauseMenuItem_Click);

                // PauseMenuItem
                this.RunInUserSessioneMenuItem.Name = "RunInUserSessioneMenuItem";
                this.RunInUserSessioneMenuItem.Size = new Size(152, 22);
                this.RunInUserSessioneMenuItem.Text = "Run Miners In User Session";
                this.RunInUserSessioneMenuItem.Click += new EventHandler(this.RunInUserSessioneMenuItem_Click);

                // IgnoreFullscreenMenuItem
                this.IgnoreFullscreenMenuItem.Name = "IgnoreFullscreenMenuItem";
                this.IgnoreFullscreenMenuItem.Size = new Size(152, 22);
                this.IgnoreFullscreenMenuItem.Text = "Ignore Fullscreen App: ";
                this.IgnoreFullscreenMenuItem.Click += new EventHandler(this.IgnoreFullscreenMenuItem_Click);
                this.IgnoreFullscreenMenuItem.Visible = false;

                TrayIconContextMenu.ResumeLayout(true);
                TrayIcon.ContextMenuStrip = TrayIconContextMenu;

                TrayIcon.Visible = true;
            }
        }

        private void OnApplicationExit(object sender, EventArgs e)
        {
            StopIdleMon();
        }

        private void OnClientConnected(NamedPipeConnection<IdleMessage, IdleMessage> connection)
        {
            if (!connectedToService)
            {
                Utilities.Log(string.Format("idleMon Client {0} is now connected!", connection.Id));

                //On connection, we'll send a MD5 hash of the MachineGUID+UserName+Current system time
                SendPipeMessage(PacketID.Authenticate, false, Utilities.GenerateAuthString(Environment.UserName), Environment.UserName, PacketID.Authenticate, connection);
            }
            else
            {
                Utilities.Log("New connection, but already connected to service?");
                connection.Close();
            }
        }

        private void OnClientDisconnected(NamedPipeConnection<IdleMessage, IdleMessage> connection)
        {
            Utilities.Log(string.Format("idleMon Client {0} has disconnected.", connection.Id));

            miningPaused = false;
            fullscreenDetected = false;
            connectedToService = false;

            try
            {
                authenticatedClients.Remove(connection);
            }
            catch { }

            fullscreenTimer.Stop();
            timer.Stop();
        }

        private void OnClientMessage(NamedPipeConnection<IdleMessage, IdleMessage> connection, IdleMessage message)
        {
            if (!authenticatedClients.Contains(connection) && message.packetId != (int)PacketID.Authenticate)
            {
                Utilities.Log($"{connection.Name}: has not authenticated, and sending non-auth first packet; closing pipe.");
                connection.Close();
            }

            switch (message.packetId)
            {
                case (int)PacketID.Authenticate:

                    if (Utilities.VerifyAuthString(message.data, message.data2))
                    {
                        Utilities.Log($"{connection.Name}: {message.data2} has authenticated successfully.");
                        authenticatedClients.Add(connection);

                        timer.Start();
                        if (monitorFullscreen) fullscreenTimer.Start();
                        connectedToService = true;

                        SendPipeMessage(PacketID.Hello, Utilities.IsIdle(), Environment.UserName, "", PacketID.None, connection);
                    }
                    else
                    {
                        Utilities.Log($"{connection.Name}: incorrect authentication packet; closing pipe.");
                        connection.Close();
                    }
                    break;

                case (int)PacketID.RunProgram:
                    Utilities.Log($"Running program: {message.data} {message.data2}");

                    Utilities.LaunchProcess(message.data, message.data2);

                    break;

                case (int)PacketID.RunInUserSession:

                    Utilities.Log($"RunInUserSession received: {message.isIdle}");

                    RunInUserSession = message.isIdle;

                    if (!message.isIdle)
                        RunInUserSessioneMenuItem.Text = "Run miners on the Desktop";
                    else
                        RunInUserSessioneMenuItem.Text = "Run miners in the System session";

                    break;

                case ((int)PacketID.Pause):
                    Utilities.Log("Pause received from MiningService.");
                    PauseMining(true);
                    break;

                case ((int)PacketID.Resume):
                    Utilities.Log("Resume received from MiningService.");
                    PauseMining(false);
                    break;

                case ((int)PacketID.IdleTime):

                    int minutes = Int32.Parse(message.data);

                    if (minutes < 0 || minutes > 3600)
                    {
                        Utilities.minutesIdle = 10;
                    }
                    else
                    {
                        Utilities.minutesIdle = minutes;
                    }

                    Utilities.Log("IdleTime received from MiningService: " + Utilities.minutesIdle);

                    break;

                case ((int)PacketID.Stealth):
                    Utilities.Log("Stealth received from MiningService: " + message.isIdle);

                    //stealthMode = message.isIdle;
                    //if (stealthMode)

                    //InitializeComponent();
                    /*
                    if (Program.stealthMode) {
                        Utilities.Log("Stealth initialized.");
                    } else
                    {
                        TrayIcon.Visible = false;
                    }
                    */
                    break;

                case ((int)PacketID.Log):
                    if (message.isIdle)
                    {
                        enableLogging = message.isIdle;
                        Utilities.Log("Logging initialized.");
                    }
                    break;

                case ((int)PacketID.Message):
                    if (TrayIcon != null)
                    {
                        ShowNotification(message.data, ToolTipIcon.None, 1000);
                    }
                    break;

                case ((int)PacketID.Stop):

                    StopIdleMon();
                    break;

                case ((int)PacketID.Fullscreen):
                    if (message.isIdle)
                    {
                        monitorFullscreen = message.isIdle;
                        fullscreenTimer.Start();
                        Utilities.Log("Fullscreen monitoring initialized.");
                    }
                    break;

                case ((int)PacketID.CheckFullscreenStillRunning):
                    if (message.isIdle)
                    {
                        CheckFullscreenStillRunning = message.isIdle;
                        fullscreenTimer.Start();
                        Utilities.Log("Received CheckFullscreenStillRunning: " + message.isIdle);
                    }
                    break;

                case ((int)PacketID.IgnoreFullscreenApp):

                    Utilities.ignoredFullscreenApps.Add(message.data);
                    //Utilities.Log("Received IgnoreFullscreenApp: " + message.data);
                    break;

                case ((int)PacketID.Notifications):

                    Utilities.ShowDesktopNotifications = message.isIdle;

                    break;

                case ((int)PacketID.Hello):

                    if (message.requestId == (int)PacketID.Pause)
                    {
                        PauseMining(stateToSet: true, showTrayNotification: false);
                        if (TrayIcon != null)
                        {
                            ShowNotification("Mining is currently Paused.", ToolTipIcon.None, 1000);
                        }
                    }

                    if (message.requestId == (int)PacketID.Resume)
                    {
                        PauseMining(stateToSet: false, showTrayNotification: false);
                    }
                    break;
            }
        }

        private void OnError(Exception exception)
        {
            Utilities.Log("idlePipe Err: " + exception.Message);

            timer.Stop();
            connectedToService = false;
        }

        private void OnFullscreenTimer(object sender, ElapsedEventArgs e)
        {
            string fullscreenApp;
            fullscreenApp = Utilities.IsForegroundFullScreen();

            if (fullscreenApp == string.Empty)
            {
                if (fullscreenDetected == true)
                {
                    if (CheckFullscreenStillRunning &&
                        Utilities.IsProcessRunning(Utilities.fullscreenAppName) &&
                        !Utilities.ignoredFullscreenApps.Contains(Utilities.fullscreenAppName))
                    {
                        //Last app is still running
                        //Utilities.Log($"CheckFullscreenStillRunning {CheckFullscreenStillRunning}: {Utilities.fullscreenAppName}");
                        return;
                    }
                    else
                    {
                        fullscreenDelay = (60000 / (int)fullscreenTimer.Interval); //should always be a 1 minute interval, even if we change the fullscreenTimer
                        fullscreenDetected = false;
                        return;
                    }
                }
            }
            else
            {
                fullscreenDetected = true;
                Utilities.fullscreenAppName = fullscreenApp;
                lock (ContextMenuLock)
                {
                    TrayIconContextMenu.SuspendLayout();
                    this.IgnoreFullscreenMenuItem.Text = "Ignore App: " + Utilities.fullscreenAppName;
                    this.IgnoreFullscreenMenuItem.Visible = true;
                    TrayIconContextMenu.ResumeLayout(true);
                    TrayIcon.ContextMenuStrip = TrayIconContextMenu;
                }
            }

            if (fullscreenDelay <= 0)
            {
                SendPipeMessage(PacketID.Fullscreen, fullscreenDetected, fullscreenApp);
            }
            else
            {
                fullscreenDelay--; //subtract 1 from the current delay before updating the service of fullscreen status
            }
        }

        private void OnTimedEvent(object source, ElapsedEventArgs e)
        {
            bool _isIdle = false;

            if (!lowOnly)
            {
                try
                {
                    _isIdle = Utilities.IsIdle();

                    if (_isIdle == Utilities.lastState)
                        return;
                }
                catch (Exception ex)
                {
                    Utilities.Log("OnTimedEvent: " + ex.Message);
                    _isIdle = false;
                    lowOnly = true;
                }
            }

            /*
            if (!miningPaused && fullscreenDetected)
            {
                SendPipeMessage(PacketID.Fullscreen, true, Environment.UserName);
                sentFirstTime = false;
            }
            */

            //If isIdle is the same as the lastState we sent, exit, unless we didn't send the first message yet.
            if (_isIdle == Utilities.lastState && sentFirstTime)
                return;

            //Send updated Idle status.
            SendPipeMessage(PacketID.Idle, _isIdle, Environment.UserName);

            sentFirstTime = true;
            Utilities.lastState = _isIdle;
        }

        private void PauseMenuItem_Click(object sender, EventArgs e)
        {
            if (this.miningPaused)
            {
                SendPipeMessage(PacketID.Resume, false, Environment.UserName, "", PacketID.Pause);
            }
            else
            {
                SendPipeMessage(PacketID.Pause, false, Environment.UserName, "", PacketID.Pause);
            }
        }

        private void PauseMining(bool stateToSet, bool showTrayNotification = true)
        {
            this.miningPaused = (stateToSet);

            if (TrayIcon == null)
                return;

            if (stateToSet)
            {
                PauseMenuItem.Text = "Resume mining";
                if (showTrayNotification) ShowNotification("Mining has been paused.", ToolTipIcon.None, 1000);
            }
            else
            {
                PauseMenuItem.Text = "Pause mining";
                if (showTrayNotification) ShowNotification("Mining has been resumed.", ToolTipIcon.None, 1000);
            }
        }

        private void RunInUserSessioneMenuItem_Click(object sender, EventArgs e)
        {
            SendPipeMessage(PacketID.RunInUserSession, !RunInUserSession, Environment.UserName, "", PacketID.Pause);
            RunInUserSession = !RunInUserSession;

            if (TrayIcon == null)
                return;

            if (!RunInUserSession)
                RunInUserSessioneMenuItem.Text = "Run miners on the Desktop (visible)";
            else
                RunInUserSessioneMenuItem.Text = "Run miners in the System session (hidden)";
        }

        private void SendPipeMessage(PacketID packetId, bool isIdle = false, string data = "", string data2 = "", PacketID requestId = PacketID.None, NamedPipeConnection<IdleMessage, IdleMessage> connection = null)
        {
            try
            {
                if (connection == null)
                {
                    server.PushMessage(new IdleMessage
                    {
                        packetId = (int)packetId,
                        isIdle = isIdle,
                        requestId = (int)requestId,
                        data = data,
                        data2 = data2
                    });
                }
                else
                {
                    connection.PushMessage(new IdleMessage
                    {
                        packetId = (int)packetId,
                        isIdle = isIdle,
                        requestId = (int)requestId,
                        data = data,
                        data2 = data2
                    });
                }
            }
            catch (Exception ex)
            {
                Utilities.Log("SendPipeMessage: " + ex.Message);
            }
        }

        private void ShowNotification(string message, ToolTipIcon icon, int time)
        {
            if (!Utilities.ShowDesktopNotifications)
                return;

            if (TrayIcon == null)
                return;

            TrayIcon.BalloonTipText = message;
            TrayIcon.BalloonTipIcon = icon;
            TrayIcon.ShowBalloonTip(time);
        }

        private void StopIdleMon()
        {
            //Cleanup so that the icon will be removed when the application is closed
            if (TrayIcon != null) TrayIcon.Visible = false;

            //stop the PipeServer
            server.Stop();

            //and finally, exit IdleMon
            System.Environment.Exit(0);
        }

        private void TrayIcon_DoubleClick(object sender, EventArgs e)
        {
            //Show the last BalloonTip message
            TrayIcon.ShowBalloonTip(1000);
        }

        private void TrayIcon_MouseUp(object sender, MouseEventArgs e)
        {
            //TrayIconContextMenu.AutoClose = true;
            //TrayIconContextMenu.Show(Cursor.Position);
            //TrayIconContextMenu.Focus();
        }
    }
}