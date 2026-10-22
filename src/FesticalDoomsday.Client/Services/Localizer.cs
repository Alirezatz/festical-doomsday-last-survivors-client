using System.ComponentModel;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Markup.Xaml;

namespace FesticalDoomsday.Client.Services;

/// <summary>
/// Every user visible text of the client, in English and Persian.
///
/// The window chrome reads its text through the {loc:Loc} markup extension and
/// the account cards read it through <see cref="Models.AccountSession"/>, so a
/// language change updates everything that is on screen, including the items
/// that live inside a data template.
/// </summary>
public sealed class Localizer : INotifyPropertyChanged
{
    /// <summary>The one localizer of the running application.</summary>
    public static Localizer Instance { get; } = new();

    private readonly Dictionary<string, (string English, string Persian)> _texts = new()
    {
        ["Brand"] = ("FESTICAL DOOMSDAY LAST SURVIVORS CLIENT",
            "FESTICAL DOOMSDAY LAST SURVIVORS CLIENT"),
        ["HeaderTitle"] = ("Multi-account manager", "مدیریت چند اکانت"),
        ["HeaderSubtitle"] = ("One window for every account: one shared backend, one shared console.",
            "همهٔ اکانت‌ها در یک پنجره: یک بک‌اند مشترک و یک کنسول مشترک."),

        ["SectionSetupDescription"] = ("Add account JSON files, then connect them, run alliance actions or point them at a custom server.",
            "فایل JSON اکانت‌ها را اضافه کن، سپس آن‌ها را وصل کن یا عملیات اتحاد را اجرا کن."),
        ["BuildCapture"] = ("Build from capture", "ساخت از کپچر"),
        ["ImportAccounts"] = ("Add JSON files", "افزودن فایل JSON"),
        ["BuildCaptureTip"] = ("Build an account JSON from a .pcap/.pcapng network capture",
            "ساخت JSON اکانت از فایل ضبط شبکهٔ pcap/pcapng"),
        ["ImportAccountsTip"] = ("Add existing account JSON files to the list",
            "افزودن فایل‌های JSON اکانت به فهرست"),

        ["ConnectionGroup"] = ("Connection", "اتصال"),
        ["ConnectionHint"] = ("Every account in the list", "همهٔ اکانت‌های فهرست"),
        ["ConnectAll"] = ("Connect All", "اتصال همه"),
        ["ConnectAllBusy"] = ("Connecting…", "در حال اتصال…"),
        ["DisconnectAll"] = ("Disconnect All", "قطع اتصال همه"),
        ["DisconnectAllBusy"] = ("Disconnecting…", "در حال قطع اتصال…"),

        ["AllianceGroup"] = ("Alliance", "اتحاد"),
        ["AllianceIdLabel"] = ("Alliance TAG or [TAG]Name", "TAG اتحاد یا ‎[TAG]Name‎"),
        ["AllianceIdTip"] = ("Search an Alliance TAG, for example _CC; add the name as [_CC]Creepy_Crawley to be exact",
            "جست‌وجوی TAG اتحاد، مثلاً _CC؛ برای انتخاب دقیق، نام را به شکل [_CC]Creepy_Crawley بنویس"),
        ["JoinAlliance"] = ("Join", "پیوستن"),
        ["LeaveAlliance"] = ("Leave", "خروج"),
        ["JoinAllianceTip"] = ("Search the alliance and send a join request to every connected account",
            "جست‌وجوی اتحاد و ارسال درخواست پیوستن برای همهٔ اکانت‌های متصل"),
        ["LeaveAllianceTip"] = ("Send a leave request to every connected account",
            "ارسال درخواست خروج برای همهٔ اکانت‌های متصل"),

        ["ServerGroup"] = ("Server", "سرور"),
        ["CustomServerLabel"] = ("Custom server host / IP", "میزبان یا IP سرور سفارشی"),
        ["CustomServerTip"] = ("Hostname or IP without a port; every account keeps the port from its own JSON",
            "نام میزبان یا IP بدون پورت؛ هر اکانت پورت JSON خودش را نگه می‌دارد"),
        ["UseCustomServer"] = ("Use Custom Server", "استفاده از سرور سفارشی"),
        ["UsingCustomServer"] = ("Custom Server On", "سرور سفارشی فعال"),
        ["UseCustomServerTip"] = ("Point new connections at this host. Click again to go back to the normal route.",
            "اتصال‌های جدید به این میزبان بروند. برای بازگشت به مسیر عادی، دوباره کلیک کن."),

        ["CommunityGroup"] = ("Community", "جامعه"),
        ["CommunityHint"] = ("News, guides and support", "اخبار، راهنما و پشتیبانی"),
        ["TelegramChannel"] = ("DoomsdayFTGroup", "DoomsdayFTGroup"),
        ["TelegramPlatform"] = ("TELEGRAM GROUP", "گروه تلگرام"),
        ["TelegramChannelTip"] = ("Open the Telegram group DoomsdayFTGroup in your browser",
            "گروه تلگرام DoomsdayFTGroup را در مرورگر باز کن"),
        ["YouTubeChannel"] = ("DoomsdayFT", "DoomsdayFT"),
        ["YouTubePlatform"] = ("YOUTUBE CHANNEL", "کانال یوتیوب"),
        ["YouTubeChannelTip"] = ("Open the YouTube channel DoomsdayFT in your browser",
            "کانال یوتیوب DoomsdayFT را در مرورگر باز کن"),

        ["AccountsTitle"] = ("Accounts", "اکانت‌ها"),
        ["EmptyAccountsTitle"] = ("No accounts yet", "هنوز اکانتی اضافه نشده"),
        ["EmptyAccountsDescription"] = ("Add an account JSON file, or build one from a .pcap/.pcapng network capture.",
            "یک فایل JSON اکانت اضافه کن، یا آن را از فایل ضبط شبکهٔ pcap/pcapng بساز."),
        ["EmptyAccountsHint"] = ("Account files stay on your disk; nothing is uploaded.",
            "فایل اکانت‌ها روی دیسک خودت می‌ماند؛ چیزی آپلود نمی‌شود."),

        ["ConsoleTitle"] = ("Console", "کنسول"),
        ["ConsoleEmpty"] = ("The console is empty.", "کنسول خالی است."),
        ["ClearConsole"] = ("Clear", "پاک‌کردن"),
        ["ClearConsoleTip"] = ("Remove every log line", "پاک‌کردن همهٔ خط‌های لاگ"),

        ["AccountsSummary"] = ("{0} accounts · {1} online · {2} connecting · {3} offline",
            "{0} اکانت · {1} متصل · {2} در حال اتصال · {3} قطع"),
        ["CopyConsoleTip"] = ("Copy the whole log to the clipboard", "کپی کل لاگ در کلیپ‌بورد"),

        ["StatusOnline"] = ("ONLINE", "متصل"),
        ["StatusOffline"] = ("OFFLINE", "قطع"),
        ["StatusConnecting"] = ("CONNECTING", "در حال اتصال"),
        ["ActionConnect"] = ("Connect", "اتصال"),
        ["ActionDisconnect"] = ("Disconnect", "قطع اتصال"),
        ["ActionConnecting"] = ("Connecting…", "در حال اتصال…"),
        ["RemoveAccount"] = ("Remove account", "حذف اکانت"),
        ["RemoveAccountTip"] = ("Remove this account from the list", "حذف اکانت از فهرست"),

        ["RamLabel"] = ("RAM", "حافظه"),
        ["LanguageEnglish"] = ("English", "English"),
        ["LanguagePersian"] = ("فارسی", "فارسی"),
        ["LanguageTip"] = ("Switch the language of the window", "تغییر زبان پنجره"),

        ["DialogCancel"] = ("Cancel", "انصراف"),
        ["DialogConfirm"] = ("OK", "تأیید"),
        ["DialogJoin"] = ("Join", "پیوستن"),
        ["DialogLeave"] = ("Leave", "خروج"),
        ["DialogRemove"] = ("Remove", "حذف"),
        ["DialogOverwrite"] = ("Overwrite", "جایگزینی"),
        ["DialogDisconnect"] = ("Disconnect", "قطع اتصال")
    };

    private bool _isPersian;

    /// <summary>True when the window shows Persian.</summary>
    public bool IsPersian => _isPersian;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised after the language changed, so live data can be refreshed.</summary>
    public event EventHandler? LanguageChanged;

    /// <summary>The text of one key in the current language.</summary>
    public string this[string key] =>
        _texts.TryGetValue(key, out var text) ? (_isPersian ? text.Persian : text.English) : key;

    /// <summary>Switches the language of the whole application.</summary>
    public void SetLanguage(bool persian)
    {
        var changed = _isPersian != persian;
        _isPersian = persian;
        // The notification is sent even when the language did not change: a
        // window that was built before the language was set has to catch up.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPersian)));
        if (changed)
            LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Picks one of two texts for code that is not bound to a key.</summary>
    public string T(string english, string persian) => _isPersian ? persian : english;

    /// <summary>The text of one key with its placeholders filled in.</summary>
    public string Format(string key, params object[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, this[key], arguments);
}

/// <summary>
/// Binds a control to one key of the <see cref="Localizer"/>. The binding
/// refreshes itself whenever the language changes.
/// </summary>
public sealed class LocExtension : MarkupExtension
{
    /// <summary>The key of the text inside the localizer.</summary>
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]")
        {
            Source = Localizer.Instance,
            Mode = BindingMode.OneWay
        };
}
