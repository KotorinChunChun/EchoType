namespace EchoType.Windows;

/// <summary>モデルの導入・起動・接続や実行環境に起因する障害。</summary>
internal sealed class AsrModelException(string message, Exception? inner = null) : InvalidOperationException(message, inner);
