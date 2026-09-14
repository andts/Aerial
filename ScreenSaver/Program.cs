using ScreenSaver;
using System;
using System.Windows.Forms;

namespace Aerial
{
    static class Program
    {
        /// <summary>
        /// Arguments for any Windows 98+ screensaver:
        ///
        ///   ScreenSaver.scr           - Show the Settings dialog box.
        ///   ScreenSaver.scr /c        - Show the Settings dialog box, modal to the foreground window.
        ///   ScreenSaver.scr /p &lt;HWND&gt; - Preview Screen Saver as child of window &lt;HWND&gt;.
        ///   ScreenSaver.scr /s        - Run the Screen Saver.
        ///
        /// Custom arguments:
        ///
        ///   ScreenSaver.scr /w        - Run in normal resizable window mode.
        ///   ScreenSaver.exe           - Run in normal resizable window mode.
        ///
        /// Playback modes (/s, /p, /w) run on a WPF message loop via AerialApp; the settings
        /// dialog is still WinForms and runs on the WinForms loop. The two are never started
        /// together - only one pump per process.
        /// </summary>
        /// <param name="args"></param>
        [STAThread]
        static void Main(string[] args)
        {
            // Needed for the WinForms settings dialog, including when it is opened from the
            // screensaver's gear button while WPF owns the message loop.
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Caching.Setup();
            RegSettings.MigrateIfNeeded();

            if (args.Length > 0)
            {
                string firstArgument = args[0].ToLower().Trim();
                string secondArgument = null;

                // Handle cases where arguments are separated by colon.
                // Examples: /c:1234567 or /P:1234567
                if (firstArgument.Length > 2)
                {
                    secondArgument = firstArgument.Substring(3).Trim();
                    firstArgument = firstArgument.Substring(0, 2);
                }
                else if (args.Length > 1)
                    secondArgument = args[1];

                if (firstArgument == "/c")           // Configuration mode
                {
                    ShowSettings();
                }
                else if (firstArgument == "/p")      // Preview mode
                {
                    if (secondArgument == null)
                    {
                        MessageBox.Show("Sorry, but the expected window handle was not provided.",
                            "ScreenSaver", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                        return;
                    }

                    long handle;
                    if (!long.TryParse(secondArgument, out handle))
                    {
                        MessageBox.Show("Sorry, but \"" + secondArgument +
                            "\" is not a valid window handle.", "ScreenSaver",
                            MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                        return;
                    }

                    AerialApp.RunPreview(new IntPtr(handle));
                }
                else if (firstArgument == "/s")      // Full-screen mode
                {
                    AerialApp.RunScreenSaver();
                }  else if (firstArgument == "/w") // if executable, windowed mode.
                {
                    AerialApp.RunWindowed();
                }
                else    // Undefined argument
                {
                    MessageBox.Show("Sorry, but the command line argument \"" + firstArgument +
                        "\" is not valid.", "ScreenSaver",
                        MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                }
            }
            else
            {
                if (System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName.EndsWith("exe")) // treat like /w
                {
                    AerialApp.RunWindowed();
                }
                else // No arguments - treat like /c
                {
                    ShowSettings();
                }
            }
        }

        /// <summary>
        /// Runs the settings dialog on the WinForms message loop. No WPF Application is created
        /// in this mode; the dialog's video preview is hosted through ElementHost, which does not
        /// need one.
        /// </summary>
        private static void ShowSettings()
        {
            var settings = new SettingsForm();
            settings.StartPosition = FormStartPosition.CenterScreen;
            Application.Run(settings);
        }
    }
}
