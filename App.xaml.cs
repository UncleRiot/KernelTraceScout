using Microsoft.UI.Xaml;
using System;
using System.Linq;

namespace KernelTraceScout
{
    public partial class App : Application
    {
        public App()
        {
            InitializeComponent();
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            AppLog.Initialize();
            string[] commandLine = Environment.GetCommandLineArgs().Skip(1).ToArray();
            if (CaptureWatchdog.Run(commandLine))
            {
                Exit();
                return;
            }
            var window = new MainWindow();
            window.Activate();
        }
    }
}
