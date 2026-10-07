using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace OdysseusLauncher
{
    static class Program
    {
        public static Mutex AppMutex = null;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        public const int SW_RESTORE = 9;
        public const int SW_SHOW = 5;

        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                Log("Fatal exception: " + (e.ExceptionObject != null ? e.ExceptionObject.ToString() : "null"));
            };

            string baseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
            int port = ReadPortFromEnv(Path.Combine(baseDir, ".env"), 7000);

            Log("Odysseus Control Panel started.");
            Log("Base directory: " + baseDir);
            Log("Port: " + port);

            bool isFirstInstance;
            AppMutex = new Mutex(true, "Global\\OdysseusApp_Desktop_Instance_Mutex_7000", out isFirstInstance);

            if (!isFirstInstance)
            {
                Log("Existing instance detected. Restoring Control Panel window...");
                IntPtr hWnd = FindWindow(null, "Odysseus - Control Panel");
                if (hWnd != IntPtr.Zero)
                {
                    ShowWindow(hWnd, SW_RESTORE);
                    SetForegroundWindow(hWnd);
                }
                else
                {
                    // If window handle was not found but server is alive, open window safely
                    if (LauncherService.IsPortListening(port, 400))
                    {
                        LauncherService.OpenWindow(port);
                    }
                }
                return;
            }

            try
            {
                Application.Run(new DashboardForm(baseDir, port));
            }
            finally
            {
                if (AppMutex != null)
                {
                    try { AppMutex.ReleaseMutex(); } catch { }
                    AppMutex = null;
                }
                Log("Odysseus Control Panel exited.");
            }
        }

        private static int ReadPortFromEnv(string envPath, int defaultPort)
        {
            try
            {
                if (File.Exists(envPath))
                {
                    string[] lines = File.ReadAllLines(envPath);
                    foreach (string rawLine in lines)
                    {
                        string line = rawLine.Trim();
                        if (line.StartsWith("APP_PORT=") || line.StartsWith("PORT="))
                        {
                            string val = line.Substring(line.IndexOf('=') + 1).Trim();
                            int p;
                            if (int.TryParse(val, out p)) return p;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log("Error reading .env: " + ex.Message);
            }
            return defaultPort;
        }

        public static void Log(string message)
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
                string logDir = Path.Combine(baseDir, "logs");
                if (!Directory.Exists(logDir)) Directory.CreateDirectory(logDir);
                string logPath = Path.Combine(logDir, "odysseus-launcher.log");
                string entry = string.Format("[{0:yyyy-MM-dd HH:mm:ss}] {1}{2}", DateTime.Now, message, Environment.NewLine);
                File.AppendAllText(logPath, entry, Encoding.UTF8);
            }
            catch { }
        }
    }

    public static class LauncherService
    {
        public static Process ServerProcess = null;

        public static bool IsPortListening(int port, int timeoutMs)
        {
            try
            {
                using (TcpClient client = new TcpClient())
                {
                    IAsyncResult ar = client.BeginConnect("127.0.0.1", port, null, null);
                    bool connected = ar.AsyncWaitHandle.WaitOne(timeoutMs);
                    if (!connected) return false;
                    client.EndConnect(ar);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        public static void StartServer(string baseDir, int port)
        {
            if (IsPortListening(port, 400))
            {
                Program.Log("Server is already listening on port " + port);
                return;
            }

            string pythonExe = Path.Combine(baseDir, "venv", "Scripts", "python.exe");
            if (!File.Exists(pythonExe))
            {
                pythonExe = "python.exe";
            }

            string logDir = Path.Combine(baseDir, "logs");
            if (!Directory.Exists(logDir)) Directory.CreateDirectory(logDir);
            string serverLog = Path.Combine(logDir, "odysseus-app.log");

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = pythonExe;
            psi.Arguments = "-m uvicorn app:app --host 127.0.0.1 --port " + port;
            psi.WorkingDirectory = baseDir;
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;

            Program.Log("Starting backend: " + pythonExe + " " + psi.Arguments);

            try
            {
                ServerProcess = new Process();
                ServerProcess.StartInfo = psi;
                ServerProcess.OutputDataReceived += (s, e) =>
                {
                    if (e.Data != null)
                    {
                        try { File.AppendAllText(serverLog, e.Data + Environment.NewLine, Encoding.UTF8); } catch { }
                    }
                };
                ServerProcess.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data != null)
                    {
                        try { File.AppendAllText(serverLog, e.Data + Environment.NewLine, Encoding.UTF8); } catch { }
                    }
                };

                ServerProcess.Start();
                ServerProcess.BeginOutputReadLine();
                ServerProcess.BeginErrorReadLine();
                Program.Log("Server process started with PID " + ServerProcess.Id);
            }
            catch (Exception ex)
            {
                Program.Log("ERROR starting server: " + ex.Message);
                MessageBox.Show("Failed to start server:\n" + ex.Message, "Odysseus Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        public static bool WaitForServer(int port, int timeoutSeconds)
        {
            int elapsed = 0;
            while (elapsed < timeoutSeconds * 1000)
            {
                if (IsPortListening(port, 400))
                {
                    Program.Log("Server confirmed listening on port " + port);
                    return true;
                }
                if (ServerProcess != null && ServerProcess.HasExited)
                {
                    Program.Log("Server process exited with code " + ServerProcess.ExitCode);
                    return false;
                }
                Thread.Sleep(500);
                elapsed += 500;
            }
            return false;
        }

        public static void StopServer(int port)
        {
            Program.Log("Stopping server on port " + port + "...");

            if (ServerProcess != null && !ServerProcess.HasExited)
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo();
                    psi.FileName = "taskkill";
                    psi.Arguments = "/PID " + ServerProcess.Id + " /T /F";
                    psi.CreateNoWindow = true;
                    psi.UseShellExecute = false;
                    Process.Start(psi).WaitForExit(2000);
                }
                catch { }
            }

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = "powershell";
                psi.Arguments = "-NoProfile -Command \"Get-NetTCPConnection -LocalPort " + port + " -ErrorAction SilentlyContinue | ForEach-Object { Stop-Process -Id $_.OwningProcess -Force -ErrorAction SilentlyContinue }\"";
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                Process.Start(psi).WaitForExit(3000);
            }
            catch (Exception ex)
            {
                Program.Log("Error stopping port processes: " + ex.Message);
            }

            ServerProcess = null;
            Program.Log("Server stopped successfully.");
        }

        public static void OpenWindow(int port)
        {
            // Safeguard: Never launch browser if port is not actively listening
            if (!IsPortListening(port, 400))
            {
                Program.Log("Cannot open browser: server is not listening on port " + port);
                return;
            }

            string url = "http://127.0.0.1:" + port;
            Program.Log("Opening window for " + url);

            string[] browsers = new string[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft\Edge\Application\msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Google\Chrome\Application\chrome.exe")
            };

            foreach (string exe in browsers)
            {
                if (File.Exists(exe))
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = exe,
                            Arguments = "--app=\"" + url + "\"",
                            UseShellExecute = true
                        });
                        return;
                    }
                    catch { }
                }
            }

            try
            {
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Program.Log("Error opening browser: " + ex.Message);
            }
        }
    }

    public class DashboardForm : Form
    {
        private string baseDir;
        private int port;
        private NotifyIcon trayIcon;
        private System.Windows.Forms.Timer statusTimer;

        private Label lblStatusDot;
        private Label lblStatusText;
        private LinkLabel lnkUrl;
        private Button btnStart;
        private Button btnStop;
        private Button btnOpenUI;
        private Button btnRestart;
        private CheckBox chkMinimizeToTray;
        private bool isTransitioning = false;

        public DashboardForm(string baseDir, int port)
        {
            this.baseDir = baseDir;
            this.port = port;

            InitializeComponent();
            SetupTrayIcon();

            statusTimer = new System.Windows.Forms.Timer();
            statusTimer.Interval = 1500;
            statusTimer.Tick += (s, e) => RefreshStatus();
            statusTimer.Start();

            RefreshStatus();
        }

        private void InitializeComponent()
        {
            this.Text = "Odysseus - Control Panel";
            this.Size = new Size(520, 520);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.BackColor = Color.FromArgb(24, 24, 37);
            this.ForeColor = Color.White;
            this.Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);

            string iconPath = Path.Combine(baseDir, "windows", "odysseus.ico");
            if (!File.Exists(iconPath))
            {
                iconPath = Path.Combine(baseDir, "odysseus.ico");
            }
            if (File.Exists(iconPath))
            {
                try { this.Icon = new Icon(iconPath); } catch { }
            }

            Panel mainPanel = new Panel();
            mainPanel.Dock = DockStyle.Fill;
            mainPanel.Padding = new Padding(24);
            this.Controls.Add(mainPanel);

            // Header
            PictureBox picIcon = new PictureBox();
            picIcon.Size = new Size(48, 48);
            picIcon.Location = new Point(24, 24);
            picIcon.SizeMode = PictureBoxSizeMode.Zoom;
            if (this.Icon != null) picIcon.Image = this.Icon.ToBitmap();
            mainPanel.Controls.Add(picIcon);

            Label lblTitle = new Label();
            lblTitle.Text = "Odysseus AI Workspace";
            lblTitle.Font = new Font("Segoe UI", 15f, FontStyle.Bold);
            lblTitle.ForeColor = Color.White;
            lblTitle.Location = new Point(82, 22);
            lblTitle.AutoSize = true;
            mainPanel.Controls.Add(lblTitle);

            Label lblSubtitle = new Label();
            lblSubtitle.Text = "Desktop Control Panel & Server Manager";
            lblSubtitle.Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);
            lblSubtitle.ForeColor = Color.FromArgb(166, 173, 200);
            lblSubtitle.Location = new Point(84, 52);
            lblSubtitle.AutoSize = true;
            mainPanel.Controls.Add(lblSubtitle);

            // Status Card
            Panel pnlCard = new Panel();
            pnlCard.Location = new Point(24, 88);
            pnlCard.Size = new Size(456, 100);
            pnlCard.BackColor = Color.FromArgb(30, 30, 46);
            mainPanel.Controls.Add(pnlCard);

            lblStatusDot = new Label();
            lblStatusDot.Text = "[•]";
            lblStatusDot.Font = new Font("Segoe UI", 13f, FontStyle.Bold);
            lblStatusDot.Location = new Point(16, 18);
            lblStatusDot.Size = new Size(35, 30);
            lblStatusDot.ForeColor = Color.FromArgb(248, 113, 113);
            pnlCard.Controls.Add(lblStatusDot);

            lblStatusText = new Label();
            lblStatusText.Text = "Server Stopped";
            lblStatusText.Font = new Font("Segoe UI", 12f, FontStyle.Bold);
            lblStatusText.ForeColor = Color.White;
            lblStatusText.Location = new Point(54, 18);
            lblStatusText.AutoSize = true;
            pnlCard.Controls.Add(lblStatusText);

            Label lblUrlTitle = new Label();
            lblUrlTitle.Text = "Address:";
            lblUrlTitle.ForeColor = Color.FromArgb(147, 154, 183);
            lblUrlTitle.Location = new Point(20, 60);
            lblUrlTitle.AutoSize = true;
            pnlCard.Controls.Add(lblUrlTitle);

            lnkUrl = new LinkLabel();
            lnkUrl.Text = "http://localhost:" + port;
            lnkUrl.Location = new Point(80, 60);
            lnkUrl.AutoSize = true;
            lnkUrl.LinkColor = Color.FromArgb(137, 180, 250);
            lnkUrl.ActiveLinkColor = Color.FromArgb(180, 205, 255);
            lnkUrl.LinkClicked += (s, e) => HandleOpenUI();
            pnlCard.Controls.Add(lnkUrl);

            Label lblPortInfo = new Label();
            lblPortInfo.Text = "Default Login: admin";
            lblPortInfo.ForeColor = Color.FromArgb(147, 154, 183);
            lblPortInfo.Location = new Point(310, 60);
            lblPortInfo.AutoSize = true;
            pnlCard.Controls.Add(lblPortInfo);

            // Action Buttons
            btnStart = CreateStyledButton("Start Server", Color.FromArgb(40, 167, 69), new Point(24, 206), new Size(220, 48));
            btnStart.Click += (s, e) => ActionStartServer(false);
            mainPanel.Controls.Add(btnStart);

            btnStop = CreateStyledButton("Stop Server", Color.FromArgb(220, 53, 69), new Point(260, 206), new Size(220, 48));
            btnStop.Click += (s, e) => ActionStopServer();
            mainPanel.Controls.Add(btnStop);

            btnOpenUI = CreateStyledButton("Open App Window", Color.FromArgb(59, 130, 246), new Point(24, 266), new Size(330, 44));
            btnOpenUI.Click += (s, e) => HandleOpenUI();
            mainPanel.Controls.Add(btnOpenUI);

            btnRestart = CreateStyledButton("Restart", Color.FromArgb(69, 71, 90), new Point(366, 266), new Size(114, 44));
            btnRestart.Click += (s, e) => ActionRestartServer();
            mainPanel.Controls.Add(btnRestart);

            // Divider
            Label lblDivider = new Label();
            lblDivider.BorderStyle = BorderStyle.Fixed3D;
            lblDivider.Location = new Point(24, 328);
            lblDivider.Size = new Size(456, 2);
            mainPanel.Controls.Add(lblDivider);

            // Tools Row
            Button btnFolder = CreateSecondaryButton("Open Folder", new Point(24, 344), new Size(144, 36));
            btnFolder.Click += (s, e) => { try { Process.Start("explorer.exe", baseDir); } catch { } };
            mainPanel.Controls.Add(btnFolder);

            Button btnLogs = CreateSecondaryButton("View Logs", new Point(180, 344), new Size(144, 36));
            btnLogs.Click += (s, e) =>
            {
                string logFile = Path.Combine(baseDir, "logs", "odysseus-app.log");
                if (!File.Exists(logFile)) File.WriteAllText(logFile, "Odysseus log initialized." + Environment.NewLine);
                try { Process.Start("notepad.exe", logFile); } catch { }
            };
            mainPanel.Controls.Add(btnLogs);

            Button btnEnv = CreateSecondaryButton("Edit .env", new Point(336, 344), new Size(144, 36));
            btnEnv.Click += (s, e) =>
            {
                string envFile = Path.Combine(baseDir, ".env");
                if (File.Exists(envFile)) { try { Process.Start("notepad.exe", envFile); } catch { } }
            };
            mainPanel.Controls.Add(btnEnv);

            // Minimize to Tray Checkbox
            chkMinimizeToTray = new CheckBox();
            chkMinimizeToTray.Text = "Minimize to System Tray on close (keeps running in background)";
            chkMinimizeToTray.ForeColor = Color.FromArgb(166, 173, 200);
            chkMinimizeToTray.Font = new Font("Segoe UI", 9f);
            chkMinimizeToTray.Location = new Point(26, 400);
            chkMinimizeToTray.Size = new Size(450, 24);
            chkMinimizeToTray.Checked = true;
            mainPanel.Controls.Add(chkMinimizeToTray);
        }

        private Button CreateStyledButton(string text, Color backColor, Point loc, Size sz)
        {
            Button btn = new Button();
            btn.Text = text;
            btn.Location = loc;
            btn.Size = sz;
            btn.BackColor = backColor;
            btn.ForeColor = Color.White;
            btn.FlatStyle = FlatStyle.Flat;
            btn.FlatAppearance.BorderSize = 0;
            btn.Font = new Font("Segoe UI", 10.5f, FontStyle.Bold);
            btn.Cursor = Cursors.Hand;
            return btn;
        }

        private Button CreateSecondaryButton(string text, Point loc, Size sz)
        {
            Button btn = new Button();
            btn.Text = text;
            btn.Location = loc;
            btn.Size = sz;
            btn.BackColor = Color.FromArgb(49, 50, 68);
            btn.ForeColor = Color.FromArgb(205, 214, 244);
            btn.FlatStyle = FlatStyle.Flat;
            btn.FlatAppearance.BorderSize = 0;
            btn.Font = new Font("Segoe UI", 9f, FontStyle.Regular);
            btn.Cursor = Cursors.Hand;
            return btn;
        }

        private void SetupTrayIcon()
        {
            ContextMenu menu = new ContextMenu();

            MenuItem miShow = new MenuItem("Show Control Panel", (s, e) => RestoreFromTray());
            miShow.DefaultItem = true;
            menu.MenuItems.Add(miShow);

            menu.MenuItems.Add(new MenuItem("Open App Window", (s, e) => HandleOpenUI()));

            menu.MenuItems.Add(new MenuItem("-"));

            menu.MenuItems.Add(new MenuItem("Start Server", (s, e) => ActionStartServer(false)));
            menu.MenuItems.Add(new MenuItem("Stop Server", (s, e) => ActionStopServer()));
            menu.MenuItems.Add(new MenuItem("Restart Server", (s, e) => ActionRestartServer()));

            menu.MenuItems.Add(new MenuItem("-"));

            menu.MenuItems.Add(new MenuItem("Exit (Stop Everything)", (s, e) =>
            {
                chkMinimizeToTray.Checked = false;
                LauncherService.StopServer(port);
                if (trayIcon != null)
                {
                    trayIcon.Visible = false;
                    trayIcon.Dispose();
                }
                Application.Exit();
            }));

            trayIcon = new NotifyIcon();
            trayIcon.Icon = this.Icon ?? SystemIcons.Application;
            trayIcon.Text = "Odysseus AI Workspace";
            trayIcon.ContextMenu = menu;
            trayIcon.Visible = true;

            trayIcon.DoubleClick += (s, e) => RestoreFromTray();
        }

        private void RestoreFromTray()
        {
            this.Show();
            this.WindowState = FormWindowState.Normal;
            this.BringToFront();
            this.Activate();
        }

        private void RefreshStatus()
        {
            if (isTransitioning) return;

            bool isRunning = LauncherService.IsPortListening(port, 350);

            if (isRunning)
            {
                lblStatusDot.ForeColor = Color.FromArgb(52, 211, 153);
                lblStatusText.Text = "Server Running (Port " + port + ")";
                lblStatusText.ForeColor = Color.FromArgb(52, 211, 153);

                btnStart.Enabled = false;
                btnStart.BackColor = Color.FromArgb(40, 70, 50);

                btnStop.Enabled = true;
                btnStop.BackColor = Color.FromArgb(220, 53, 69);

                btnOpenUI.Text = "Open App Window";
                btnOpenUI.BackColor = Color.FromArgb(59, 130, 246);

                btnRestart.Enabled = true;
                btnRestart.BackColor = Color.FromArgb(69, 71, 90);

                lnkUrl.Enabled = true;
            }
            else
            {
                lblStatusDot.ForeColor = Color.FromArgb(248, 113, 113);
                lblStatusText.Text = "Server Stopped";
                lblStatusText.ForeColor = Color.FromArgb(248, 113, 113);

                btnStart.Enabled = true;
                btnStart.BackColor = Color.FromArgb(40, 167, 69);

                btnStop.Enabled = false;
                btnStop.BackColor = Color.FromArgb(70, 40, 45);

                btnOpenUI.Text = "Start Server & Open Window";
                btnOpenUI.BackColor = Color.FromArgb(59, 130, 246);

                btnRestart.Enabled = false;
                btnRestart.BackColor = Color.FromArgb(40, 42, 54);

                lnkUrl.Enabled = false;
            }
        }

        private void HandleOpenUI()
        {
            if (LauncherService.IsPortListening(port, 350))
            {
                LauncherService.OpenWindow(port);
            }
            else
            {
                // Auto-start server and open window once ready!
                ActionStartServer(true);
            }
        }

        private void ActionStartServer(bool openUiWhenReady)
        {
            isTransitioning = true;
            lblStatusDot.ForeColor = Color.FromArgb(251, 191, 36);
            lblStatusText.Text = "Starting Server...";
            lblStatusText.ForeColor = Color.FromArgb(251, 191, 36);
            btnStart.Enabled = false;
            btnStop.Enabled = false;

            ThreadPool.QueueUserWorkItem((state) =>
            {
                LauncherService.StartServer(baseDir, port);
                bool ready = LauncherService.WaitForServer(port, 45);

                this.Invoke((MethodInvoker)delegate
                {
                    isTransitioning = false;
                    RefreshStatus();
                    if (ready)
                    {
                        if (trayIcon != null)
                            trayIcon.ShowBalloonTip(2000, "Odysseus", "Server is ready on port " + port, ToolTipIcon.Info);

                        if (openUiWhenReady)
                        {
                            LauncherService.OpenWindow(port);
                        }
                    }
                    else
                    {
                        MessageBox.Show("Server startup timed out. Check logs\\odysseus-app.log for errors.", "Odysseus", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                });
            });
        }

        private void ActionStopServer()
        {
            isTransitioning = true;
            lblStatusDot.ForeColor = Color.FromArgb(251, 191, 36);
            lblStatusText.Text = "Stopping Server...";
            lblStatusText.ForeColor = Color.FromArgb(251, 191, 36);
            btnStart.Enabled = false;
            btnStop.Enabled = false;

            ThreadPool.QueueUserWorkItem((state) =>
            {
                LauncherService.StopServer(port);

                this.Invoke((MethodInvoker)delegate
                {
                    isTransitioning = false;
                    RefreshStatus();
                    if (trayIcon != null)
                        trayIcon.ShowBalloonTip(1500, "Odysseus", "Server stopped.", ToolTipIcon.Info);
                });
            });
        }

        private void ActionRestartServer()
        {
            isTransitioning = true;
            lblStatusDot.ForeColor = Color.FromArgb(251, 191, 36);
            lblStatusText.Text = "Restarting Server...";
            lblStatusText.ForeColor = Color.FromArgb(251, 191, 36);
            btnStart.Enabled = false;
            btnStop.Enabled = false;

            ThreadPool.QueueUserWorkItem((state) =>
            {
                LauncherService.StopServer(port);
                Thread.Sleep(1000);
                LauncherService.StartServer(baseDir, port);
                bool ready = LauncherService.WaitForServer(port, 45);

                this.Invoke((MethodInvoker)delegate
                {
                    isTransitioning = false;
                    RefreshStatus();
                });
            });
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (chkMinimizeToTray != null && chkMinimizeToTray.Checked && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                this.Hide();
                if (trayIcon != null)
                {
                    trayIcon.ShowBalloonTip(2000, "Odysseus", "Control Panel minimized to System Tray. Double-click tray icon to restore.", ToolTipIcon.Info);
                }
                return;
            }

            if (trayIcon != null)
            {
                trayIcon.Visible = false;
                trayIcon.Dispose();
                trayIcon = null;
            }
            LauncherService.StopServer(port);
            base.OnFormClosing(e);
        }
    }
}
