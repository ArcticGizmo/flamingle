namespace Flamingle.App.Services;

/// <summary>
/// News worth a Windows notification rather than a toast: it arrives in the background (an update check, a load that
/// took a while), or it matters enough to wait in the Notification Center (a file that couldn't be written). Toasts are
/// for answering something the user just did. With <c>WindowsNotifications</c> off it falls back to a toast.
/// </summary>
/// <param name="Text">Never empty — Windows drops a notification with no text.</param>
/// <param name="Warning">Something went wrong (shown with the warning icon).</param>
internal sealed record Notification(string Title, string Text, bool Warning = false);
