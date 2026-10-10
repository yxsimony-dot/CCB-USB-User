namespace UsbWebLauncher.App.Browser;

// A narrow adapter around WebView's download event preserves its original request
// while allowing the asynchronous ownership and cancellation boundary to be tested.
internal interface IReceiptDownloadSource : IDisposable
{
    event EventHandler<IReceiptDownloadStarting>? DownloadStarting;
}

internal interface IReceiptDownloadStarting
{
    IReceiptDownloadOperation Operation { get; }
    string ResultFilePath { get; set; }
    bool Handled { get; set; }
    bool Cancel { get; set; }
    Action GetCompleteDeferral();
}

internal enum ReceiptDownloadState { InProgress, Completed, Interrupted }

internal interface IReceiptDownloadOperation
{
    string Uri { get; }
    string MimeType { get; }
    ReceiptDownloadState State { get; }
    event EventHandler? StateChanged;
    void Cancel();
}
