using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace ScreenKeepAlive
{
    static class Program
    {
        private static Mutex mutex = null;

        [STAThread]
        static void Main()
        {
            // ป้องกันการเปิดโปรแกรมซ้ำซ้อน (Single Instance)
            bool createdNew;
            mutex = new Mutex(true, "ScreenKeepAlive_SingleInstance_Mutex_D_Drive", out createdNew);
            if (!createdNew)
            {
                MessageBox.Show("โปรแกรมกันจอดับกำลังทำงานอยู่แล้ว\nคุณสามารถคลิกที่ไอคอนบริเวณ System Tray (มุมขวาล่างใกล้ๆ นาฬิกา) เพื่อเปิดดูหน้าต่างได้",
                                "แจ้งเตือน", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    // COM Interfaces สำหรับสร้างไฟล์ .lnk (ทางลัด) บน Windows
    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    internal class ShellLink { }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    internal interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, out IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    public class MainForm : Form
    {
        // Windows API ควบคุมระบบพลังงานหน้าจอ
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern uint SetThreadExecutionState(uint esFlags);

        // Windows API จำลองการขยับเมาส์และกดปุ่มแบบไร้ผลกระทบ
        [DllImport("user32.dll")]
        private static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        private const uint ES_SYSTEM_REQUIRED  = 0x00000001;
        private const uint ES_DISPLAY_REQUIRED = 0x00000002;
        private const uint ES_CONTINUOUS       = 0x80000000;

        private const uint MOUSEEVENTF_MOVE    = 0x0001;
        private const uint KEYEVENTF_KEYUP     = 0x0002;
        private const byte VK_F15              = 0x7E; // คีย์ F15 (ไม่มีตัวอักษรพิมพ์ออกมา ไม่กวนการทำงาน)

        // ระยะเวลาต่อรอบ: 4 นาที 50 วินาที = 290 วินาที
        private const int CYCLE_SECONDS = 290;

        // UI Components
        private Panel panelStatus;
        private Label lblStatusIcon;
        private Label lblStatusText;
        private Label lblCountdown;
        private Label lblLastAction;
        private Label lblActionCount;
        private Button btnStart;
        private Button btnStop;
        private Button btnActionNow;
        private Button btnCreateShortcut;
        private CheckBox chkMinimizeToTray;
        private NotifyIcon notifyIcon;
        private ContextMenu trayMenu;

        private System.Windows.Forms.Timer countdownTimer;
        private int remainingSeconds = CYCLE_SECONDS;
        private int totalActions = 0;
        private bool isRunning = false;

        public MainForm()
        {
            InitializeComponent();
            StartKeepAlive(); // เริ่มทำงานอัตโนมัติทันทีเมื่อเปิดโปรแกรม
        }

        private void InitializeComponent()
        {
            this.Text = "โปรแกรมกันจอดับ (Screen Keep-Alive) - ทุก 4 นาที 50 วิ";
            this.Size = new Size(480, 430);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.BackColor = Color.FromArgb(248, 249, 250);
            this.Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);

            // ไอคอนโปรแกรม
            string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
            if (File.Exists(iconPath))
            {
                try { this.Icon = new Icon(iconPath); } catch { }
            }

            // === แถบสถานะด้านบน (Status Banner) ===
            panelStatus = new Panel();
            panelStatus.Location = new Point(20, 16);
            panelStatus.Size = new Size(424, 52);
            panelStatus.BorderStyle = BorderStyle.None;
            panelStatus.Paint += (s, e) =>
            {
                Color borderColor = isRunning ? Color.FromArgb(165, 214, 167) : Color.FromArgb(224, 224, 224);
                ControlPaint.DrawBorder(e.Graphics, panelStatus.ClientRectangle, borderColor, ButtonBorderStyle.Solid);
            };

            lblStatusIcon = new Label();
            lblStatusIcon.Location = new Point(14, 13);
            lblStatusIcon.Size = new Size(26, 26);
            lblStatusIcon.Font = new Font("Segoe UI", 13f, FontStyle.Bold);
            lblStatusIcon.TextAlign = ContentAlignment.MiddleCenter;

            lblStatusText = new Label();
            lblStatusText.Location = new Point(44, 14);
            lblStatusText.Size = new Size(365, 24);
            lblStatusText.Font = new Font("Segoe UI", 11f, FontStyle.Bold);
            lblStatusText.TextAlign = ContentAlignment.MiddleLeft;

            panelStatus.Controls.Add(lblStatusIcon);
            panelStatus.Controls.Add(lblStatusText);
            this.Controls.Add(panelStatus);

            // === การ์ดแสดงข้อมูลการทำงาน (Information Card) ===
            Panel panelCard = new Panel();
            panelCard.Location = new Point(20, 78);
            panelCard.Size = new Size(424, 140);
            panelCard.BackColor = Color.White;
            panelCard.BorderStyle = BorderStyle.None;
            panelCard.Paint += (s, e) =>
            {
                ControlPaint.DrawBorder(e.Graphics, panelCard.ClientRectangle, Color.FromArgb(230, 230, 230), ButtonBorderStyle.Solid);
            };

            Label lblCycleInfo = new Label();
            lblCycleInfo.Location = new Point(16, 12);
            lblCycleInfo.Size = new Size(390, 20);
            lblCycleInfo.Text = "⏱️ รอบการแอ็กชัน: ทุก 4 นาที 50 วินาที (290 วินาที)";
            lblCycleInfo.ForeColor = Color.FromArgb(70, 70, 70);
            panelCard.Controls.Add(lblCycleInfo);

            Label lblTimerTitle = new Label();
            lblTimerTitle.Location = new Point(16, 38);
            lblTimerTitle.Size = new Size(180, 24);
            lblTimerTitle.Text = "⏳ แอ็กชันครั้งถัดไปในอีก:";
            lblTimerTitle.ForeColor = Color.FromArgb(70, 70, 70);
            panelCard.Controls.Add(lblTimerTitle);

            lblCountdown = new Label();
            lblCountdown.Location = new Point(200, 32);
            lblCountdown.Size = new Size(200, 32);
            lblCountdown.Font = new Font("Consolas", 18f, FontStyle.Bold);
            lblCountdown.ForeColor = Color.FromArgb(27, 94, 32);
            lblCountdown.Text = "04:50";
            panelCard.Controls.Add(lblCountdown);

            lblLastAction = new Label();
            lblLastAction.Location = new Point(16, 76);
            lblLastAction.Size = new Size(390, 20);
            lblLastAction.Text = "🕒 แอ็กชันล่าสุดเมื่อ: ยังไม่มี (เริ่มรอบแรก)";
            lblLastAction.ForeColor = Color.FromArgb(90, 90, 90);
            panelCard.Controls.Add(lblLastAction);

            lblActionCount = new Label();
            lblActionCount.Location = new Point(16, 104);
            lblActionCount.Size = new Size(390, 20);
            lblActionCount.Text = "📊 จำนวนครั้งที่แอ็กชันแล้ว: 0 ครั้ง";
            lblActionCount.ForeColor = Color.FromArgb(90, 90, 90);
            panelCard.Controls.Add(lblActionCount);

            this.Controls.Add(panelCard);

            // === ปุ่มหลัก: เริ่ม / หยุด ===
            btnStart = new Button();
            btnStart.Location = new Point(20, 230);
            btnStart.Size = new Size(205, 46);
            btnStart.Text = "▶  เริ่มทำงาน";
            btnStart.Font = new Font("Segoe UI", 11.5f, FontStyle.Bold);
            btnStart.BackColor = Color.FromArgb(46, 125, 50);
            btnStart.ForeColor = Color.White;
            btnStart.FlatStyle = FlatStyle.Flat;
            btnStart.FlatAppearance.BorderSize = 0;
            btnStart.Cursor = Cursors.Hand;
            btnStart.Click += (s, e) => StartKeepAlive();
            this.Controls.Add(btnStart);

            btnStop = new Button();
            btnStop.Location = new Point(239, 230);
            btnStop.Size = new Size(205, 46);
            btnStop.Text = "⏹  หยุดทำงาน";
            btnStop.Font = new Font("Segoe UI", 11.5f, FontStyle.Bold);
            btnStop.BackColor = Color.FromArgb(198, 40, 40);
            btnStop.ForeColor = Color.White;
            btnStop.FlatStyle = FlatStyle.Flat;
            btnStop.FlatAppearance.BorderSize = 0;
            btnStop.Cursor = Cursors.Hand;
            btnStop.Click += (s, e) => StopKeepAlive();
            this.Controls.Add(btnStop);

            // === ปุ่มตัวช่วยเสริม ===
            btnActionNow = new Button();
            btnActionNow.Location = new Point(20, 288);
            btnActionNow.Size = new Size(205, 34);
            btnActionNow.Text = "⚡ ทดสอบแอ็กชันทันที";
            btnActionNow.Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);
            btnActionNow.BackColor = Color.FromArgb(238, 238, 238);
            btnActionNow.ForeColor = Color.FromArgb(50, 50, 50);
            btnActionNow.FlatStyle = FlatStyle.Flat;
            btnActionNow.FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200);
            btnActionNow.Cursor = Cursors.Hand;
            btnActionNow.Click += (s, e) => TriggerAction(manual: true);
            this.Controls.Add(btnActionNow);

            btnCreateShortcut = new Button();
            btnCreateShortcut.Location = new Point(239, 288);
            btnCreateShortcut.Size = new Size(205, 34);
            btnCreateShortcut.Text = "📌 สร้างทางลัดบน Desktop";
            btnCreateShortcut.Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);
            btnCreateShortcut.BackColor = Color.FromArgb(238, 238, 238);
            btnCreateShortcut.ForeColor = Color.FromArgb(50, 50, 50);
            btnCreateShortcut.FlatStyle = FlatStyle.Flat;
            btnCreateShortcut.FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200);
            btnCreateShortcut.Cursor = Cursors.Hand;
            btnCreateShortcut.Click += (s, e) => CreateDesktopShortcut();
            this.Controls.Add(btnCreateShortcut);

            // Checkbox ย่อลง System Tray
            chkMinimizeToTray = new CheckBox();
            chkMinimizeToTray.Location = new Point(24, 336);
            chkMinimizeToTray.Size = new Size(420, 24);
            chkMinimizeToTray.Text = "ย่อลง System Tray มุมล่างขวา เมื่อกดปิดหน้าต่าง (X)";
            chkMinimizeToTray.Checked = true;
            chkMinimizeToTray.ForeColor = Color.FromArgb(90, 90, 90);
            this.Controls.Add(chkMinimizeToTray);

            // Timer นับถอยหลังทุก 1 วินาที
            countdownTimer = new System.Windows.Forms.Timer();
            countdownTimer.Interval = 1000;
            countdownTimer.Tick += CountdownTimer_Tick;

            // เมนูคลิกขวาใน System Tray
            trayMenu = new ContextMenu();
            trayMenu.MenuItems.Add("เปิดหน้าต่างโปรแกรม", (s, e) => RestoreFromTray());
            trayMenu.MenuItems.Add("เริ่มทำงาน (Start)", (s, e) => StartKeepAlive());
            trayMenu.MenuItems.Add("หยุดทำงาน (Stop)", (s, e) => StopKeepAlive());
            trayMenu.MenuItems.Add("-");
            trayMenu.MenuItems.Add("ปิดโปรแกรมอย่างสมบูรณ์", (s, e) => ExitApplication());

            notifyIcon = new NotifyIcon();
            notifyIcon.ContextMenu = trayMenu;
            notifyIcon.Visible = true;
            notifyIcon.Icon = this.Icon != null ? this.Icon : SystemIcons.Application;
            notifyIcon.DoubleClick += (s, e) => RestoreFromTray();

            this.FormClosing += MainForm_FormClosing;
        }

        private void StartKeepAlive()
        {
            isRunning = true;
            remainingSeconds = CYCLE_SECONDS;

            // ส่งคำสั่งป้องกันหน้าจอดับและป้องกันเครื่อง Sleep ไปยัง Windows Kernel
            SetThreadExecutionState(ES_CONTINUOUS | ES_DISPLAY_REQUIRED | ES_SYSTEM_REQUIRED);

            countdownTimer.Start();
            UpdateUIState();
        }

        private void StopKeepAlive()
        {
            isRunning = false;
            countdownTimer.Stop();

            // คืนค่าพลังงานสู่ปกติ
            SetThreadExecutionState(ES_CONTINUOUS);

            UpdateUIState();
        }

        private void CountdownTimer_Tick(object sender, EventArgs e)
        {
            if (!isRunning) return;

            remainingSeconds--;
            if (remainingSeconds <= 0)
            {
                TriggerAction(manual: false);
                remainingSeconds = CYCLE_SECONDS;
            }

            int mins = remainingSeconds / 60;
            int secs = remainingSeconds % 60;
            lblCountdown.Text = string.Format("{0:D2}:{1:D2}", mins, secs);

            string trayText = string.Format("โปรแกรมกันจอดับ: ทำงานอยู่ ({0:D2}:{1:D2})", mins, secs);
            if (trayText.Length > 63) trayText = trayText.Substring(0, 63);
            notifyIcon.Text = trayText;
        }

        private void TriggerAction(bool manual)
        {
            try
            {
                // 1) สั่ง Windows OS ป้องกันจอดับ
                SetThreadExecutionState(ES_CONTINUOUS | ES_DISPLAY_REQUIRED | ES_SYSTEM_REQUIRED);

                // 2) ขยับเมาส์สัมพันธ์ 1 พิกเซลและดึงกลับทันทีในระดับไมโครวินาที
                // เมาส์จะอยู่ที่ตำแหน่งเดิมเสมอ 100% ไม่เลื่อนไปที่อื่น
                mouse_event(MOUSEEVENTF_MOVE, 1, 0, 0, UIntPtr.Zero);
                mouse_event(MOUSEEVENTF_MOVE, -1, 0, 0, UIntPtr.Zero);

                // 3) จำลองการกดปุ่ม F15 เสมือน เพื่อรีเซ็ต Idle Timer สำหรับระบบที่ล็อกจอตามเวลาไม่ได้พิมพ์
                // ปุ่ม F15 เป็นปุ่มมาตรฐานใน Windows ที่ไม่มีตัวอักษรใดๆ ไม่แทรกในการพิมพ์งาน
                keybd_event(VK_F15, 0, 0, UIntPtr.Zero);
                keybd_event(VK_F15, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);

                totalActions++;
                lblLastAction.Text = string.Format("🕒 แอ็กชันล่าสุดเมื่อ: {0} {1}",
                    DateTime.Now.ToString("HH:mm:ss"),
                    manual ? "(กดทดสอบเอง)" : "(อัตโนมัติ)");
                lblActionCount.Text = string.Format("📊 จำนวนครั้งที่แอ็กชันแล้ว: {0} ครั้ง", totalActions);

                if (manual)
                {
                    remainingSeconds = CYCLE_SECONDS;
                    int mins = remainingSeconds / 60;
                    int secs = remainingSeconds % 60;
                    lblCountdown.Text = string.Format("{0:D2}:{1:D2}", mins, secs);
                }
            }
            catch (Exception ex)
            {
                lblLastAction.Text = "⚠️ เกิดข้อผิดพลาด: " + ex.Message;
            }
        }

        private void UpdateUIState()
        {
            if (isRunning)
            {
                panelStatus.BackColor = Color.FromArgb(232, 245, 233);
                lblStatusIcon.Text = "🟢";
                lblStatusIcon.ForeColor = Color.FromArgb(46, 125, 50);
                lblStatusText.Text = "กำลังทำงาน (ระบบกันจอดับเปิดอยู่)";
                lblStatusText.ForeColor = Color.FromArgb(46, 125, 50);

                lblCountdown.ForeColor = Color.FromArgb(27, 94, 32);
                int mins = remainingSeconds / 60;
                int secs = remainingSeconds % 60;
                lblCountdown.Text = string.Format("{0:D2}:{1:D2}", mins, secs);

                btnStart.Enabled = false;
                btnStart.BackColor = Color.FromArgb(170, 205, 170);
                btnStop.Enabled = true;
                btnStop.BackColor = Color.FromArgb(198, 40, 40);

                notifyIcon.Text = "โปรแกรมกันจอดับ: กำลังทำงาน";
            }
            else
            {
                panelStatus.BackColor = Color.FromArgb(245, 245, 245);
                lblStatusIcon.Text = "⚪";
                lblStatusIcon.ForeColor = Color.FromArgb(117, 117, 117);
                lblStatusText.Text = "หยุดทำงานชั่วคราว (กดปุ่มเริ่มเพื่อทำงาน)";
                lblStatusText.ForeColor = Color.FromArgb(117, 117, 117);

                lblCountdown.ForeColor = Color.FromArgb(117, 117, 117);
                lblCountdown.Text = "--:--";

                btnStart.Enabled = true;
                btnStart.BackColor = Color.FromArgb(46, 125, 50);
                btnStop.Enabled = false;
                btnStop.BackColor = Color.FromArgb(220, 170, 170);

                notifyIcon.Text = "โปรแกรมกันจอดับ: หยุดทำงาน";
            }
            panelStatus.Invalidate();
        }

        private void CreateDesktopShortcut()
        {
            try
            {
                IShellLinkW link = (IShellLinkW)new ShellLink();
                link.SetDescription("โปรแกรมป้องกันหน้าจอดับ (Screen Keep-Alive)");
                link.SetPath(Application.ExecutablePath);
                link.SetWorkingDirectory(AppDomain.CurrentDomain.BaseDirectory);
                string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
                if (File.Exists(iconPath))
                {
                    link.SetIconLocation(iconPath, 0);
                }

                IPersistFile file = (IPersistFile)link;
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                string shortcutPath = Path.Combine(desktop, "โปรแกรมกันจอดับ.lnk");
                file.Save(shortcutPath, false);

                MessageBox.Show("สร้างทางลัดบนหน้าจอ Desktop เรียบร้อยแล้ว!",
                                "สำเร็จ", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("เกิดข้อผิดพลาดในการสร้างทางลัด: " + ex.Message,
                                "ข้อผิดพลาด", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (chkMinimizeToTray.Checked && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                this.Hide();
                notifyIcon.ShowBalloonTip(2000, "โปรแกรมกันจอดับ",
                    "โปรแกรมยังคงทำงานอย่างต่อเนื่องใน System Tray (มุมล่างขวา)\nดับเบิลคลิกไอคอนเพื่อเปิดหน้าต่าง",
                    ToolTipIcon.Info);
            }
            else
            {
                ExitApplication();
            }
        }

        private void RestoreFromTray()
        {
            this.Show();
            this.WindowState = FormWindowState.Normal;
            this.BringToFront();
            this.Activate();
        }

        private void ExitApplication()
        {
            StopKeepAlive();
            if (notifyIcon != null)
            {
                notifyIcon.Visible = false;
                notifyIcon.Dispose();
            }
            Application.ExitThread();
            Environment.Exit(0);
        }
    }
}
