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
        ["Version"] = ("v2.0.0", "v2.0.0"),
        ["VersionTip"] = ("Festical Doomsday Last Survivors Client version 2.0.0",
            "نسخهٔ ۲.۰.۰ کلاینت Festical Doomsday Last Survivors"),

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
        ["GuildUnknown"] = ("Alliance: unknown", "اتحاد: نامشخص"),
        ["GuildNone"] = ("Alliance: none — Auto Help waits for a join",
            "اتحاد: بدون اتحاد — کمک خودکار منتظر پیوستن است"),
        ["GuildStatus"] = ("Alliance: {0}", "اتحاد: {0}"),
        ["GuildStatusTip"] = ("The alliance the server reports for this account",
            "اتحادی که سرور برای این اکانت گزارش می‌کند"),
        ["HelpCountTip"] = ("Alliance helps this account has completed since the app started",
            "تعداد کمک‌های این اکانت به هم‌اتحادی‌ها از زمان شروع برنامه"),
        ["BountyTicketTip"] = ("Bounty Ticket balance; the green +N is what the last Bounty Ground match paid — even the weakest finish pays 1",
            "موجودی Bounty Ticket؛ عدد سبز +N پاداش آخرین بازی Bounty Ground است؛ حتی ضعیف‌ترین نتیجه هم ۱ می‌دهد"),

        ["BountyOn"] = ("Bounty ON", "Bounty ON"),
        ["BountyOff"] = ("Bounty OFF", "Bounty OFF"),
        ["BountyChipTip"] = ("Bounty mode of this account: ON while it keeps starting Bounty matches",
            "وضعیت Bounty این اکانت: روشن تا وقتی بازی Bounty شروع می‌شود"),
        ["BountyUnavailableTip"] = ("Bounty OFF: the game rejected the registration with error 14000 (below the Battle Royale power requirement, or no usable hero/troop). This account is not retried; the other accounts continue.",
            "Bounty خاموش: سرور ثبت‌نام را با خطای 14000 رد کرد (قدرت کمتر از حد لازم Battle Royale یا نداشتن قهرمان/نیروی قابل استفاده). برای این اکانت دیگر تلاش نمی‌شود؛ بقیهٔ اکانت‌ها ادامه می‌دهند."),
        ["SpinStart"] = ("Spin the Bounty wheel with the available Bounty Tickets (100 each, up to 5 per turn)",
            "چرخاندن گردونه با Bounty Ticketهای موجود (هر دور ۱۰۰، حداکثر ۵ دور در هر بار)"),
        ["SpinStop"] = ("Stop the Bounty wheel", "توقف گردونه"),
        ["MightLabel"] = ("Might", "قدرت"),
        ["LevelLabel"] = ("Level", "سطح"),
        ["SteelLabel"] = ("STEEL", "فولاد"),
        ["FoodLabel"] = ("FOOD", "غذا"),
        ["WoodLabel"] = ("WOOD", "چوب"),
        ["OilLabel"] = ("OIL", "نفت"),
        ["Information"] = ("Information", "اطلاعات"),
        ["InformationTip"] = ("Account information: name, Might, level and the resources the server reports",
            "اطلاعات اکانت: نام، قدرت، سطح و منابعی که سرور گزارش می‌کند"),
        ["Bag"] = ("Bag", "کیف"),
        ["BagTip"] = ("Open the real in-game bag: the resource stacks it holds, and Use them",
            "بازکردن کیف واقعی بازی: موجودی منابع داخل آن و استفاده از آن‌ها"),
        ["BagTitle"] = ("Account information", "اطلاعات اکانت"),
        ["BagSubtitle"] = ("Identity, Might and the resources of this account",
            "هویت، قدرت و منابع این اکانت"),
        ["BagResources"] = ("RESOURCES", "منابع"),
        ["BagWindowTitle"] = ("Game bag", "کیف بازی"),
        ["BagWindowStacks"] = ("RESOURCE STACKS IN THE BAG", "موجودی منابع داخل کیف"),
        ["BagStacksCount"] = ("{0} stacks", "{0} موجودی"),
        ["BagWaiting"] = ("Waiting for the game server to report the bag…",
            "در انتظار گزارش کیف از سرور بازی…"),
        ["BagNoStacks"] = ("No usable resource stack in the bag yet.",
            "هنوز موجودی منابع قابل استفاده‌ای در کیف نیست."),
        ["BagUse"] = ("Use", "استفاده"),
        ["BagUseAll"] = ("Use all", "استفاده از همه"),
        ["BagUseTip"] = ("Use one stack: the server moves its resources into the city",
            "استفاده از یک عدد: سرور منابع آن را به شهر منتقل می‌کند"),
        ["BagUseAllTip"] = ("Use every copy of this stack",
            "استفاده از تمام موجودی این ردیف"),
        ["BagConnectHint"] = ("Connect this account to read and use its bag.",
            "برای دیدن و استفاده از کیف، اکانت را وصل کن."),
        ["BagUseHint"] = ("These are the real stacks of the game bag. Use moves a stack's resources into the city; the green numbers above are what the bag holds right now.",
            "این‌ها موجودی واقعی کیف بازی هستند. با «استفاده» منابع به شهر منتقل می‌شوند؛ اعداد سبز بالا جمع موجودی کیف هستند."),
        ["BagOtherGroup"] = ("ITEM", "آیتم"),
        ["BountyTicketsLabel"] = ("BOUNTY TICKETS", "BOUNTY TICKET"),
        ["BagIdentity"] = ("ACCOUNT", "اکانت"),
        ["BagEmpty"] = ("No account data yet: connect the account to fill the bag.",
            "هنوز اطلاعاتی از اکانت نیست: برای پر شدن کیف، اکانت را وصل کن."),
        ["BagSourceHint"] = ("These numbers come from the game server for this account.",
            "این اعداد از سرور بازی برای همین اکانت گرفته شده‌اند."),
        ["CloseWindow"] = ("Close", "بستن"),
        ["AutoBounty"] = ("Auto Start Bounty Game", "شروع خودکار بازی Bounty"),
        ["AutoBountyOn"] = ("Auto Bounty: ON", "Bounty خودکار: روشن"),
        ["AutoBountyTip"] = ("Keep starting Battle Royale (Bounty) for every connected account with 1 troop and 1 hero, waiting a human-like delay after each match",
            "برای همهٔ اکانت‌های متصل، بازی Battle Royale (Bounty) را پشت سر هم با ۱ نیرو و ۱ قهرمان شروع کن و بعد از هر بازی تأخیر انسانی بده"),

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
        ["LogLevelOff"] = ("Log: Off", "لاگ: خاموش"),
        ["LogLevelNormal"] = ("Log: Normal", "لاگ: معمولی"),
        ["LogLevelDebug"] = ("Log: Debug", "لاگ: کامل"),
        ["LogLevelTip"] = ("Console detail, click to cycle. Off writes nothing, skips the logging work entirely and hides the panel; Normal logs events only; Debug also logs every decoded game message, which is what you need to report a protocol problem.",
            "میزان جزئیات کنسول؛ با کلیک عوض می‌شود. خاموش: هیچ چیز نوشته نمی‌شود، کار لاگ انجام نمی‌شود و پنل هم پنهان می‌ماند؛ معمولی: فقط رویدادها؛ کامل: تمام پیام‌های decode‌شدهٔ بازی هم نوشته می‌شوند."),
        ["ConsoleShow"] = ("Show console", "نمایش کنسول"),
        ["ConsoleHide"] = ("Hide console", "پنهان‌کردن کنسول"),
        ["ConsoleToggleTip"] = ("Show or hide the console panel; logging itself stays at the level the Log button sets",
            "نمایش یا پنهان‌کردن پنل کنسول؛ سطح لاگ همان است که دکمهٔ Log تعیین می‌کند"),

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

        ["TransportTip"] = ("Transport Resource: send food, wood, steel or oil to another Commander through this account's Trading Post",
            "ارسال منابع: فرستادن غذا، چوب، فولاد یا نفت به یک فرماندهٔ دیگر با Trading Post همین اکانت"),
        ["TransportNoBuilding"] = ("You have not built a Trading Post yet. Build it first, then this account can send resources.",
            "تو هنوز ساختمان Trading Post را نساخته‌ای. اول آن را بساز تا این اکانت بتواند منابع بفرستد."),
        ["TransportNoBuildingShort"] = ("Not built", "ساخته نشده"),
        ["TransportNoBuildingHelp"] = ("Build the Trading Post in the city, then reopen this form.",
            "ساختمان Trading Post را در شهر بساز و بعد این فرم را دوباره باز کن."),
        ["TransportLevelShort"] = ("Trading Post Lv {0}", "Trading Post سطح {0}"),
        ["TransportTitle"] = ("Transport Resource", "ارسال منابع"),
        ["TransportSubtitle"] = ("Trading Post · {0}", "Trading Post · {0}"),
        ["TransportNoBuildingBanner"] = ("Commander, you cannot supply resources as you have not built a Trading Post yet.",
            "فرمانده، تو هنوز ساختمان Trading Post را نساخته‌ای، پس نمی‌توانی منابع بفرستی."),
        ["TransportReceiver"] = ("RECEIVER", "گیرنده"),
        ["TransportReceiverId"] = ("Commander ID", "شناسهٔ فرمانده"),
        ["TransportCheck"] = ("Check", "بررسی"),
        ["TransportNearby"] = ("CASTLES OF THIS REGION", "قلعه‌های همین ریجن"),
        ["TransportNearbyHint"] = ("Click a castle to fill its Commander ID",
            "برای پر شدن شناسه، روی یک قلعه کلیک کن"),
        ["TransportNoNearby"] = ("No castle loaded yet. Connect the account; the castles of the region arrive with the map.",
            "هنوز قلعه‌ای بارگذاری نشده. اکانت را وصل کن؛ قلعه‌های ریجن همراه نقشه می‌آیند."),
        ["TransportLimits"] = ("TRADING POST", "ساختمان Trading Post"),
        ["TransportPerSend"] = ("Transportation Capacity", "ظرفیت هر ارسال"),
        ["TransportDailySupply"] = ("Daily Supply Limit", "سقف ارسال روزانه"),
        ["TransportDailyReceive"] = ("Daily Receiving Limit", "سقف دریافت روزانه"),
        ["TransportTaxRate"] = ("Tax Rate", "نرخ مالیات"),
        ["TransportSpeed"] = ("Transport Speed Up", "سرعت ارسال"),
        ["TransportResource"] = ("RESOURCE", "نوع منبع"),
        ["TransportAmount"] = ("AMOUNT", "مقدار"),
        ["TransportAvailable"] = ("Available in the city: {0}", "موجود در شهر: {0}"),
        ["TransportOneTrip"] = ("One transport carries at most {0}", "هر ارسال حداکثر {0}"),
        ["TransportSend"] = ("Send", "ارسال"),
        ["TransportSending"] = ("Sending…", "در حال ارسال…"),
        ["TransportMax"] = ("Max", "حداکثر"),
        ["TransportTaxLine"] = ("Tax {0} · {1}", "مالیات {0} · {1}"),
        ["TransportReceiveLine"] = ("the receiver gets {0}", "گیرنده {0} می‌گیرد"),
        ["TransportTargetSelf"] = ("this account itself", "خود همین اکانت"),
        ["TransportTargetLine"] = ("{0} · Trading Post Lv {1}", "{0} · Trading Post سطح {1}"),
        ["TransportTargetReceive"] = ("Received today: {0} of {1}", "دریافت‌شدهٔ امروز: {0} از {1}"),
        ["TransportTargetRemaining"] = ("It can still receive {0} today", "امروز {0} دیگر می‌تواند دریافت کند"),
        ["TransportSupplyUsed"] = ("Sent today: {0} of {1}", "ارسال‌شدهٔ امروز: {0} از {1}"),
        ["TransportNeedTarget"] = ("Enter the Commander ID of the receiver.", "شناسهٔ فرماندهٔ گیرنده را وارد کن."),
        ["TransportTargetNoBuilding"] = ("The target does not have a Trading Post. Unable to send resources.",
            "گیرنده هنوز Trading Post ندارد و نمی‌تواند منابع بگیرد."),
        ["TransportTargetNotFound"] = ("That Commander is not among the castles this session loaded. Open the map around them once, then try again.",
            "این فرمانده بین قلعه‌های بارگذاری‌شدهٔ این نشست نیست. یک بار نقشه را کنارش باز کن و دوباره تلاش کن."),
        ["TransportNeedConnect"] = ("Connect this account before sending resources.",
            "برای ارسال منابع، اول اکانت را وصل کن."),
        ["TransportDone"] = ("Sent {0} to {1}.", "{0} برای {1} ارسال شد."),
        ["TransportRefused"] = ("The game server refused the transport (error {0}).",
            "سرور بازی ارسال را رد کرد (خطای {0})."),
        ["TransportNotEnough"] = ("This account does not have that many resources in the city.",
            "این مقدار منبع در شهر این اکانت موجود نیست."),
        ["TransportOverCapacity"] = ("One transport carries at most {0} with this Trading Post level.",
            "با این سطح Trading Post، هر ارسال حداکثر {0} است."),
        ["TransportErrorNoBuilding"] = ("You have not built a Trading Post yet.",
            "تو هنوز Trading Post نساخته‌ای."),
        ["TransportErrorTargetNoBuilding"] = ("The receiver has no Trading Post.",
            "گیرنده Trading Post ندارد."),
        ["TransportErrorSupplyLimit"] = ("The Daily Supply Limit of this account is reached; it resets at 00:00 UTC.",
            "سقف ارسال روزانهٔ این اکانت پر شده؛ ساعت ۰۰:۰۰ UTC صفر می‌شود."),
        ["TransportErrorCapacity"] = ("The amount is above the Transportation Capacity of this Trading Post.",
            "مقدار بیشتر از ظرفیت ارسال این Trading Post است."),
        ["TransportErrorResource"] = ("That resource cannot be transported.",
            "این نوع منبع قابل ارسال نیست."),
        ["TransportErrorWorld"] = ("The receiver is outside this region.",
            "گیرنده بیرون از این ریجن است."),
        ["TransportErrorTrading"] = ("The Trading Post refused the order; try again.",
            "Trading Post این دستور را رد کرد؛ دوباره تلاش کن."),

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
