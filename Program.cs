using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Text;

internal static class Program
{
    
    // 修改为实际需要保留的天数。包含当天，例如 7 表示保留当天及前 6 天。
    //private const int RetentionDays = 7;

  /* private static readonly string[] RootDirectories =
   {
       @"E:\本地图像",
       @"G:\本地图像"
   };*/

    private static readonly object LogLock = new();

    private static StreamWriter? _logWriter;

    private static int Main()
    {
        string exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
        string exeDir = System.IO.Path.GetDirectoryName(exePath);
        string iniFilePath =System.IO.Path.Combine(exeDir, "config.ini");
        ConfigModel cfg = ReadMyIni(iniFilePath);
        string[] RootDirectories = new string[2];
        
        //若为空，给默认值
        if ((cfg.FolderList).Count==0)
        {
            RootDirectories = new string[2] {@"E:\本地图像", @"G:\本地图像" };
        }
       else
        {
            int i = 0;
            foreach (var item in cfg.FolderList)
            {
                Console.WriteLine(item);
                RootDirectories[i] = item;
                i=i + 1;
            }

        }
        int RetentionDays;
        if (cfg.SaveDays == 0)
        {
            RetentionDays =30;
        }
        else
        {
            RetentionDays = cfg.SaveDays;
        }

        DateTime startTime = DateTime.Now;
        DateTime today = DateTime.Today;
        DateTime thresholdDate = today.AddDays(-(RetentionDays - 1));

        //获取代码地址
        string appDirectory = AppContext.BaseDirectory;
        string appName = Path.GetFileNameWithoutExtension(
            Environment.ProcessPath ?? AppDomain.CurrentDomain.FriendlyName)+"log";

        if (string.IsNullOrWhiteSpace(appName))
        {
            appName = "ImageFolderCleaner";
        }

        string logDirectory = Path.Combine(appDirectory, appName);
        string logFile = Path.Combine(logDirectory, $"{today:yyyy-MM-dd}.log");

        try
        {
            Directory.CreateDirectory(logDirectory);

            // UTF-8 BOM，Windows 记事本打开中文不会乱码。
            _logWriter = new StreamWriter(
                logFile,
                append: true,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true))
            {
                AutoFlush = true
            };
            WriteLog(exeDir);
            WriteLog("========== 脚本开始 ==========");
            WriteLog($"开始时间：{startTime:yyyy-MM-dd HH:mm:ss}");
            WriteLog($"保留天数：{RetentionDays} 天（包含当天）");
            WriteLog($"日期阈值：{thresholdDate:yyyy-MM-dd}，早于此日期的目录将删除");
            WriteLog($"处理根目录：{string.Join("；", RootDirectories)}");

            if (RetentionDays < 1)
            {
                WriteLog("警告：保留天数必须大于等于 1，程序未执行清理。");
                RetentionDays = 30;
            }
            foreach (string rootDirectory in RootDirectories)
            {
                ProcessRootDirectory(rootDirectory, thresholdDate);
            }

            WriteLog($"结束时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            WriteLog("========== 脚本结束 ==========");

            return 0;
        }
        catch (Exception ex)
        {
            // 极端情况，例如日志目录都无法创建时，仍然保证程序有明确退出码。
            Console.Error.WriteLine($"程序发生未处理异常：{ex}");
            return 1;
        }
        finally
        {
            _logWriter?.Dispose();
        }
    }

    private static void ProcessRootDirectory(string rootDirectory, DateTime thresholdDate)
    {   
           
        if (!Directory.Exists(rootDirectory))
        {
            WriteLog($"警告：根目录不存在，跳过。路径：{rootDirectory}");
            return;
        }

        WriteLog($"开始处理根目录：{rootDirectory}");

        try
        {
            // 流式枚举：不会一次性把全部子目录加载到内存。
            foreach (string directoryPath in Directory.EnumerateDirectories(
                         rootDirectory,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                ProcessDateDirectory(directoryPath, thresholdDate);
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            WriteLog($"警告：无法枚举根目录，权限不足，跳过该根目录。路径：{rootDirectory}；原因：{ex.Message}");
        }
        catch (DirectoryNotFoundException ex)
        {
            WriteLog($"警告：枚举时根目录不存在，跳过该根目录。路径：{rootDirectory}；原因：{ex.Message}");
        }
        catch (IOException ex)
        {
            WriteLog($"警告：枚举根目录发生 I/O 异常，跳过该根目录。路径：{rootDirectory}；原因：{ex.Message}");
        }
        catch (Exception ex)
        {
            WriteLog($"警告：枚举根目录发生未知异常，跳过该根目录。路径：{rootDirectory}；原因：{ex.Message}");
        }
    }

    private static void ProcessDateDirectory(string directoryPath, DateTime thresholdDate)
    {
        string directoryName = Path.GetFileName(directoryPath);

        // 仅处理名称严格符合 yyyy-MM-dd 的一级子目录。
        if (!DateTime.TryParseExact(
                directoryName,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateTime folderDate))
        {
            WriteLog($"保留：目录名不是 yyyy-MM-dd 日期格式，不处理。路径：{directoryPath}");
            return;
        }

        try
        {
            // 先判断空目录。空日期目录不受保留期限限制，直接删除。
            if (IsDirectoryEmpty(directoryPath))
            {
                WriteLog($"待删除：日期文件夹为空。路径：{directoryPath}");
                DeleteDirectory(directoryPath);
                return;
            }

            if (folderDate.Date < thresholdDate)
            {
                WriteLog(
                    $"待删除：目录日期 {folderDate:yyyy-MM-dd} 早于阈值 {thresholdDate:yyyy-MM-dd}。路径：{directoryPath}");

                DeleteDirectory(directoryPath);
            }
            else
            {
                WriteLog(
                    $"保留：目录日期 {folderDate:yyyy-MM-dd} 在保留范围内。路径：{directoryPath}");
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            WriteLog($"失败跳过：权限不足。路径：{directoryPath}；原因：{ex.Message}");
        }
        catch (DirectoryNotFoundException)
        {
            // 可能被其他程序或人工在处理过程中删除。
            WriteLog($"保留：目录在处理过程中已不存在。路径：{directoryPath}");
        }
        catch (IOException ex)
        {
            WriteLog($"失败跳过：目录或内部文件可能被程序占用。路径：{directoryPath}；原因：{ex.Message}");
        }
        catch (Exception ex)
        {
            WriteLog($"失败跳过：处理目录时发生未知异常。路径：{directoryPath}；原因：{ex.Message}");
        }
    }

    private static bool IsDirectoryEmpty(string directoryPath)
    {
        // 只读取第一项即可判定，不枚举或缓存全部内容。
        using IEnumerator<string> enumerator = Directory.EnumerateFileSystemEntries(
            directoryPath,
            "*",
            SearchOption.TopDirectoryOnly).GetEnumerator();

        return !enumerator.MoveNext();
    }

    private static void DeleteDirectory(string directoryPath)
    {
        try
        {
            // 仅传入根目录下的日期子目录，绝不传入根目录本体。
            Directory.Delete(directoryPath, recursive: true);
            WriteLog($"成功删除：{directoryPath}");
        }
        catch (UnauthorizedAccessException ex)
        {
            WriteLog($"失败跳过：删除权限不足。路径：{directoryPath}；原因：{ex.Message}");
        }
        catch (IOException ex)
        {
            WriteLog($"失败跳过：删除失败，目录或文件可能被占用。路径：{directoryPath}；原因：{ex.Message}");
        }
        catch (Exception ex)
        {
            WriteLog($"失败跳过：删除时发生未知异常。路径：{directoryPath}；原因：{ex.Message}");
        }
    }

    private static void WriteLog(string message)
    {
        string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";

        lock (LogLock)
        {
            Console.WriteLine(line);
            _logWriter?.WriteLine(line);
        }
    }
    public static ConfigModel ReadMyIni(string iniPath)
    {
        ConfigModel res = new ConfigModel();

        //判断文件是否存在
        if (!File.Exists(iniPath))
            return res;

        //如果ini中文乱码，改成 Encoding.UTF8
        string[] allLines = File.ReadAllLines(iniPath, System.Text.Encoding.Default);

        foreach (var rawLine in allLines)
        {
            string line = rawLine.Trim();
            //跳过空行
            if (string.IsNullOrWhiteSpace(line)) continue;
            //按等号切割
            string[] kv = line.Split('=');
            if (kv.Length < 2) continue;

            string key = kv[0].Trim();
            string value = kv[1].Trim();

            switch (key)
            {
                case "文件夹路径":
                    //按分号拆分多条路径，自动剔除空项
                    var arr = value.Split(';', StringSplitOptions.RemoveEmptyEntries);
                    res.FolderList.AddRange(arr);
                    break;
                case "保留天数":
                    //安全转换，失败默认0，不会程序崩溃
                    int.TryParse(value, out int days);
                    res.SaveDays = days;
                    break;
            }
        }
        return res;
    }
}
public class ConfigModel
{
    /// <summary>多个文件夹路径</summary>
    public List<string> FolderList { get; set; } = new List<string>();
    /// <summary>图像保留天数</summary>
    public int SaveDays { get; set; }
}
