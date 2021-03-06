﻿using System;
using System.Collections.Generic;
using System.Windows.Forms;
using NiceHashMiner.Utils;
using NiceHashMiner.Configs;
using NiceHashMiner.Forms;
using NiceHashMiner.Enums;
using Newtonsoft.Json;
using System.Globalization;
using System.Threading;

namespace NiceHashMiner
{
    static class Program
    {
        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main(string[] argv)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
            //Console.OutputEncoding = System.Text.Encoding.Unicode;
            // #0 set this first so data parsing will work correctly
            Globals.JsonSettings = new JsonSerializerSettings {
                NullValueHandling = NullValueHandling.Ignore,
                MissingMemberHandling = MissingMemberHandling.Ignore,
                Culture = CultureInfo.InvariantCulture
            };

            // #1 first initialize config
            ConfigManager.InitializeConfig();

            if (ConfigManager.GeneralConfig.LogToFile) {
                Logger.ConfigureWithFile();
            }

            if (ConfigManager.GeneralConfig.DebugConsole) {
                Helpers.AllocConsole();
            }

            // init active display currency after config load
            CurrencyConverter.ActiveDisplayCurrency = ConfigManager.GeneralConfig.DisplayCurrency;

            // #2 then parse args
            var commandLineArgs = new CommandLineParser(argv);

            Helpers.ConsolePrint("NICEHASH", "Starting up NiceHashMiner v" + Application.ProductVersion);

            if (!ConfigManager.GeneralConfigIsFileExist() && !commandLineArgs.IsLang)
            {
                Helpers.ConsolePrint("NICEHASH", "No config file found. Running NiceHash Miner for the first time. Choosing a default language.");
                Application.Run(new Form_ChooseLanguage());
            }

            // Init languages
            International.Initialize(ConfigManager.GeneralConfig.Language);

            if (commandLineArgs.IsLang) {
                Helpers.ConsolePrint("NICEHASH", "Language is overwritten by command line parameter (-lang).");
                International.Initialize(commandLineArgs.LangValue);
                ConfigManager.GeneralConfig.Language = commandLineArgs.LangValue;
            }
            
            Application.Run(new Form_Main());
        }

    }
}
