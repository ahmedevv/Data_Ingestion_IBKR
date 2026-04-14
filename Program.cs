using System;
using System.Windows.Forms;

namespace API
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            // This launches the MainForm window we just coded
            Application.Run(new MainForm());
        }
    }
}