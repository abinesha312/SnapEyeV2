using System;
using System.Windows;
using SnapEye.Config;
using SnapEye.Services;

namespace SnapEye
{
    /// <summary>
    /// Modal popup shown when the user switches to Interview mode. Captures the target
    /// company, role, and job description so every answer can be tailored to the role.
    /// </summary>
    public partial class JobDescriptionWindow : Window
    {
        public string Company { get; private set; } = "";
        public string Role { get; private set; } = "";
        public string JobDescription { get; private set; } = "";

        public JobDescriptionWindow(string company = "", string role = "", string jobDescription = "")
        {
            InitializeComponent();
            CompanyBox.Text = company ?? "";
            RoleBox.Text = role ?? "";
            JdBox.Text = jobDescription ?? "";
            Loaded += (_, _) =>
            {
                // Belt-and-suspenders: re-assert capture exclusion once the HWND is fully
                // realized, in case it wasn't ready during OnSourceInitialized. Guarantees the
                // popup never leaks into a screen share/recording.
                try { CaptureInvisibility.ApplyToWindow(this); } catch { /* best effort */ }
                JdBox.Focus();
            };
        }

        // Always keep the interview JD popup out of screen captures / screen shares — it
        // contains the job description the candidate is preparing against, so it must never
        // be visible to a meeting the user is sharing, regardless of the global toggle.
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            try
            {
                CaptureInvisibility.ApplyToWindow(this);
            }
            catch
            {
                /* best effort */
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            Company = CompanyBox.Text?.Trim() ?? "";
            Role = RoleBox.Text?.Trim() ?? "";
            JobDescription = JdBox.Text?.Trim() ?? "";
            DialogResult = true;
            Close();
        }

        private void Skip_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
