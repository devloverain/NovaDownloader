using System;
using System.IO;
using System.Threading.Tasks;
using NovaDownloader.Core.Engine;
using NovaDownloader.Core.Models;
using NovaDownloader.Core.Configuration;

class Program
{
    static async Task Main(string[] args)
    {
        string url = "https://rr1---sn-tt1e7nl6.googlevideo.com/videoplayback?expire=1792771788&ei=bDLbauDPBZ-0ir4P05C32AI&ip=121.127.34.92&id=o-ACCFS-nlOTrq6kkwg-_Nvulbpq8ueEKHXHJ0hNgwRYUq&itag=18&source=youtube&requiressl=yes&xpc=EgVo2aDSNQ%3D%3D&cps=318&met=1792750188%2C&mh=p_&mm=18%2C29&mn=sn-tt1e7nl6%2Csn-p5qs7nd7&ms=aub%2Crdu&mv=m&mvi=1&pl=24&rms=aub%2Caub&initcwndbps=2316000&bui=AWzQHwqhuhTym0XSA1RSyLaFbsddqL4fn63edNlB8rKb5j-zZR_6eSzqUpOI2Jtw-GRe75RTRvmw7qDB&spc=mpRQ_-5ql-7qAXdpqvK29vdqEfem0djOPSRIl9SgUOhmq--cUj7BFZYMtF_ucib2VSQ&vprv=1&svpuc=1&mime=video%2Fmp4&rqh=1&cnr=14&ratebypass=yes&dur=122.183&lmt=1776510206725392&mt=1792749667&fvip=4&epbp=1&fexp=51565115&c=ANDROID&txp=5538534&sparams=expire%2Cei%2Cip%2Cid%2Citag%2Csource%2Crequiressl%2Cxpc%2Cbui%2Cspc%2Cvprv%2Csvpuc%2Cmime%2Crqh%2Ccnr%2Cratebypass%2Cdur%2Clmt&sig=AE0s2JYwRQIhAN4veSNQiVG9MTZ64FUqcb3t4c6s9m1B_XNx1M8xQgzrAiBcCbs0lMQDBFWB62HuX5EmF9U4HFWMnAW27Bl3XbrdhQ%3D%3D&lsparams=cps%2Cmet%2Cmh%2Cmm%2Cmn%2Cms%2Cmv%2Cmvi%2Cpl%2Crms%2Cinitcwndbps&lsig=APaTxxMwRQIhAM3VgQ8fOoE2mGGLCuC5ucksyKD5CV6sUgdX8USFNc9HAiBdiw31DjXRl26K-cTP_Q891XtOpdY9fbFPTQbviP5D5A%3D%3D";
        string dest = @"C:\Users\Alireza\.gemini\antigravity\scratch\NovaDownloader\nova_youtube_video_with_audio.mp4";

        var options = new DownloaderOptions
        {
            DefaultSegments = 8,
            BufferSize = 64 * 1024
        };

        var engine = new MultiSegmentDownloader(options);
        var task = new DownloadTask
        {
            Url = url,
            DestinationPath = dest,
            FileName = Path.GetFileName(dest)
        };

        engine.ProgressUpdated += (s, e) =>
        {
            Console.WriteLine($"Progress: {e.ProgressPercentage:F1}% | Speed: {e.SpeedBytesPerSec / 1024.0:F1} KB/s | Downloaded: {e.DownloadedBytes}/{e.TotalBytes}");
        };

        engine.TaskCompleted += (s, t) =>
        {
            Console.WriteLine($"[SUCCESS] Download completed successfully: {t.DestinationPath}");
        };

        Console.WriteLine("Starting Multi-Segment Download with NovaDownloader.Core...");
        await engine.DownloadAsync(task);
    }
}
