using Death_Counter.Properties;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace Death_Counter
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    /// 

    public partial class GameModel
    {
        public string ProcessName;
        public string CleanName;
        public long BaseAddress;
        public List<Int32> Offsets;
        public List<long> LongOffsets;
        public bool IsBigEndian;
    }

    public partial class MainWindow : Window
    {
        [DllImport("kernel32.dll", SetLastError = true, CallingConvention = CallingConvention.Winapi)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWow64Process([In] IntPtr process, [Out] out bool wow64Process);

        [DllImport("kernel32.dll")]
        private static extern IntPtr OpenProcess(int dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, [Out] byte[] lpBuffer, int dwSize, out int lpNumberOfBytesRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr hProcess, Int64 lpBaseAddress, [Out] byte[] lpBuffer, int dwSize, out int lpNumberOfBytesRead);

        private List<GameModel> gameOffsets;

        private GameModel currentGame;

        private Process[] gameProcess;

        private bool canOutputFile;
        private bool gameIsRunning;
        private bool isWow64;

        private int refreshRate;
        private int currentDeaths;

        private Task memRead;

        private CancellationTokenSource cancelTokenSource;

        public MainWindow()
        {
            InitializeComponent();

            InitOffsetsAndNames();

            if (!string.IsNullOrEmpty(Settings.Default.FileOutput)) tbOutputFile.Text = Settings.Default.FileOutput;
            if (!string.IsNullOrEmpty(Settings.Default.RefreshRate)) tbRefreshRate.Text = Settings.Default.RefreshRate;
        }

        private void FileSave(object sender, RoutedEventArgs e)
        {
            SaveFileDialog fileDialog = new SaveFileDialog();
            fileDialog.Filter = "Text file|*.txt";
            fileDialog.Title = "Locate where to save the text file.";

            if (fileDialog.ShowDialog() == true)
            {
                Settings.Default.FileOutput = fileDialog.FileName;
                Settings.Default.Save();
                Settings.Default.Reload();

                tbOutputFile.Text = fileDialog.FileName;
            }
        }

        private void StartCounting(object sender, RoutedEventArgs e)
        {
            gameIsRunning = GetProcess();

            if (gameIsRunning)
            {
                if (memRead != null)
                {
                    cancelTokenSource.Cancel();
                    cancelTokenSource.Dispose();
                    memRead.Wait();
                }

                canOutputFile = !string.IsNullOrEmpty(tbOutputFile.Text);

                HandleGame();
            }
            else
            {
                if (memRead != null)
                {
                    cancelTokenSource.Cancel();
                    cancelTokenSource.Dispose();
                    memRead.Wait();
                }

                StatusUpdate("No game detected or missing output file", 0, false);
            }
        }

        private void TbRefreshRate_TextChanged(object sender, TextChangedEventArgs e)
        {
            var tb = (TextBox)sender;
            refreshRate = Convert.ToInt32(tb.Text);

            Settings.Default.RefreshRate = tb.Text;
            Settings.Default.Save();
            Settings.Default.Reload();
        }

        private void NumValidator(object sender, TextCompositionEventArgs e)
        {
            Regex regex = new Regex("[^0-9]+");
            e.Handled = regex.IsMatch(e.Text);
        }

        private void InitOffsetsAndNames()
        {
            // You're not a programmer until you can 
            // comfortably do things in silly ways.
            gameOffsets = new List<GameModel>
            {
                new GameModel
                {
                    ProcessName = "rpcs3",
                    CleanName = "Demon's Souls (RPCS3)",
                    BaseAddress = 0x300000000,
                    LongOffsets = new List<long>
                    {
                        0x3301E76B4
                    },
                    IsBigEndian = true
                },
                new GameModel
                {
                    ProcessName = "DARKSOULS",
                    CleanName = "Dark Souls: PTD",
                    Offsets = new List<Int32>
                    {
                        0xF78700,
                        0x5C
                    }
                },
                new GameModel
                {
                    ProcessName = "DarkSoulsRemastered",
                    CleanName = "Dark Souls Remastered",
                    Offsets = new List<Int32>
                    {
                        0x1D278F0,
                        0x98
                    }
                },
                new GameModel
                {
                    ProcessName = "DarkSoulsII",
                    CleanName = "Dark Souls II",
                    Offsets = new List<Int32>
                    {
                        0x160B8D0,
                        0xD0,
                        0x390,
                        0x294
                    }
                },
                new GameModel
                {
                    ProcessName = "DarkSoulsIII",
                    CleanName = "Dark Souls III",
                    Offsets = new List<Int32>
                    {
                        0x4740178,
                        0x98
                    }
                },
                new GameModel
                {
                    ProcessName = "nioh",
                    CleanName = "Nioh",
                    Offsets = new List<Int32>
                    {
                        0x231BC88
                    }
                },
                new GameModel
                {
                    ProcessName = "sekiro",
                    CleanName = "Sekiro: Shadows Die Twice",
                    Offsets = new List<Int32>
                    {
                        0x3B48D30,
                        0x90
                    }
                },

            };
        }

        private bool GetProcess()
        {
            for (var i = 0; i < gameOffsets.Count; i++)
            {
                gameProcess = Process.GetProcessesByName(gameOffsets[i].ProcessName);

                if (gameProcess.Length != 0)
                {
                    currentGame = gameOffsets[i];

                    //Console.WriteLine("Found game: " + gameOffsets[i].Name);
                    return true;
                } 
                else
                {
                    //Console.WriteLine("Could not find game: " + gameOffsets[i].Name);
                }
            }

            return false;
        }

        private void HandleGame()
        {
            cancelTokenSource = new CancellationTokenSource();
            var cancelToken = cancelTokenSource.Token;

            int bytesRead = 0;
            isWow64 = Is64Bit(gameProcess[0]);

            IntPtr baseAddress = gameProcess[0].MainModule.BaseAddress;

            if (currentGame.BaseAddress != 0)
            {
                baseAddress = (IntPtr)currentGame.BaseAddress;
            }

            IntPtr finalValue = baseAddress;

            // Move to recursive instead?
            if (currentGame.Offsets != null && currentGame.Offsets.Count == 1)
            {
                finalValue = IntPtr.Add(baseAddress, currentGame.Offsets[0]);
            }
            else if (currentGame.Offsets == null && currentGame.LongOffsets != null && currentGame.LongOffsets.Count == 1)
            {
                // Drit i det
                if (isWow64)
                {
                    finalValue = IntPtr.Add(baseAddress, (int)currentGame.LongOffsets[0]);
                }
                
            }
            else if (currentGame.Offsets != null)
            {
                for (var i = 0; i < currentGame.Offsets.Count; i++)
                {
                    // Do not handle the last offset here 
                    if (i + 1 < currentGame.Offsets.Count)
                    {
                        finalValue = IntPtr.Add(finalValue, currentGame.Offsets[i]);
                        if (isWow64)
                        {
                            finalValue = (IntPtr)BitConverter.ToInt64(GetMemory64(gameProcess[0], (Int64)finalValue, 8, out bytesRead), 0);
                        }
                        else
                        {
                            finalValue = (IntPtr)BitConverter.ToInt32(GetMemory32(gameProcess[0], (Int32)finalValue, 4, out bytesRead), 0);
                        }
                        
                    }
                    else
                    {
                        finalValue = IntPtr.Add(finalValue, currentGame.Offsets[i]);
                    }
                }
            }

            if (isWow64)
            {
                memRead = Task.Factory.StartNew(async () =>
                {
                    cancelToken.ThrowIfCancellationRequested();

                    while (GetProcess())
                    {
                        GetDeaths64(gameProcess[0], (Int64)finalValue, 4, out bytesRead);

                        if (cancelToken.IsCancellationRequested)
                        {
                            Application.Current.Dispatcher.Invoke(new Action(() => StatusUpdate("Currently not running", 0, false)));
                            cancelToken.ThrowIfCancellationRequested();
                        }

                        await Task.Delay(refreshRate);
                    }
                }, cancelTokenSource.Token);
            }
            else
            {
                memRead = Task.Factory.StartNew(async () =>
                {
                    cancelToken.ThrowIfCancellationRequested();

                    while (GetProcess())
                    {
                        GetDeaths32(gameProcess[0], (Int32)finalValue, 4, out bytesRead);

                        if (cancelToken.IsCancellationRequested)
                        {
                            Application.Current.Dispatcher.Invoke(new Action(() => StatusUpdate("Currently not running", 0, false)));
                            cancelToken.ThrowIfCancellationRequested();
                        }

                        await Task.Delay(refreshRate);
                    }
                }, cancelTokenSource.Token);
            }
        }

        private byte[] GetMemory32(Process process, Int32 address, int numOfBytes, out int bytesRead)
        {
            IntPtr hProc = OpenProcess(0x0010, false, process.Id);

            byte[] buffer = new byte[numOfBytes];

            ReadProcessMemory(hProc, new IntPtr(address), buffer, numOfBytes, out bytesRead);
            return buffer;
        }

        private byte[] GetMemory64(Process process, Int64 address, int numOfBytes, out int bytesRead)
        {
            IntPtr hProc = OpenProcess(0x0010, false, process.Id);

            byte[] buffer = new byte[numOfBytes];

            ReadProcessMemory(hProc, new IntPtr(address), buffer, numOfBytes, out bytesRead);

            return buffer;
        }

        private void GetDeaths32(Process process, Int32 address, int length, out int bytesRead)
        {
            byte[] memoryOutput = GetMemory32(process, address, 4, out bytesRead);
            int value = BitConverter.ToInt32(memoryOutput, 0);
            currentDeaths = value;

            HandleDeathOutput(value);
        }

        private void GetDeaths64(Process process, Int64 address, int length, out int bytesRead)
        {
            byte[] memoryOutput = GetMemory64(process, address, 4, out bytesRead);

            if (currentGame.IsBigEndian)
            {
                Console.WriteLine(address);
                Array.Reverse(memoryOutput, 0, memoryOutput.Length);
            }

            int value = BitConverter.ToInt32(memoryOutput, 0);
            currentDeaths = value;

            HandleDeathOutput(value);
        }

        private void HandleDeathOutput(int value)
        {
            var mainThreadText = new Func<string>(GetOutputTextFormat);
            var textFormat = (string)Application.Current.Dispatcher.Invoke(mainThreadText);

            Application.Current.Dispatcher.Invoke(new Action(() => StatusUpdate("Found " + currentGame.CleanName + ".", currentDeaths, true)));

            var outputString = string.Empty;

            if (textFormat.Contains("{0}"))
            {
                outputString = string.Format(textFormat, value.ToString());
            }
            else
            {
                outputString = "Missing format item ({0}) from output string";
            }

            Console.WriteLine(outputString);

            if (canOutputFile)
            {
                using (var tw = new StreamWriter(Settings.Default.FileOutput, false))
                {
                    tw.WriteLine(outputString);
                }
            }
        }

        public static bool Is64Bit(Process process)
        {
            bool isWow64;

            if (!IsWow64Process(process.Handle, out isWow64))
            {
                throw new Win32Exception();
            }
                
            return !isWow64;
        }

        private void StatusUpdate(string statusMessage, int deaths, bool processStatus)
        {
            gameIsRunning = processStatus;
            lblStatus.Content = statusMessage;
            lblDeathCounter.Content = deaths;
        }

        private string GetOutputTextFormat()
        {
            return tbOutputFormat.Text;
        }
    }
}
