using System;
using System.Collections.Generic;
using System.Globalization;

namespace HS_Reconnector.Core
{
    internal static class Localization
    {
        public static event Action LanguageChanged;

        private static readonly Dictionary<string, string> En = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AppTitle"] = "HS-Reconnector",
            ["BadgeAdmin"] = "Administrator",
            ["BadgeNoAdmin"] = "Not Elevated",
            ["StatusCardTitle"] = "Connection & Game Status",
            ["LabelGame"] = "Hearthstone",
            ["LabelEndpoint"] = "Game Server",
            ["LabelTcp"] = "TCP Socket",
            ["LabelLog"] = "Active Log",
            ["GameRunning"] = "Running",
            ["GameNotRunning"] = "Not Running",
            ["EndpointWaiting"] = "Waiting for match...",
            ["EndpointUnsupported"] = "Unsupported endpoint (non-IPv4)",
            ["OverlayUnsupported"] = "Non-IPv4",
            ["TcpEstablished"] = "Established ({0})",
            ["TcpMultiple"] = "Multiple sockets ({0})",
            ["TcpNone"] = "No active connection",
            ["TcpDisconnected"] = "Disconnecting...",
            ["LogWaiting"] = "Not attached yet",
            ["BtnReconnect"] = "Reconnect",
            ["BtnDisconnected"] = "Disconnected...",
            ["BtnWait"] = "Wait {0}s",
            ["BtnClearLog"] = "Clear",
            ["BtnSaveApply"] = "Save & Apply",
            ["BtnResetOverlay"] = "Reset Overlay Pos",
            ["BtnBrowse"] = "Browse...",
            ["BtnResetHotkey"] = "Reset",
            ["BtnClearHotkey"] = "Disable",
            ["SectionHotkey"] = "Reconnect & Overlay Settings",
            ["SectionBehavior"] = "Application & Hearthstone Path",
            ["SectionLogs"] = "Activity Log",
            ["OptHotkey"] = "Global Hotkey",
            ["OptHotkeyPrompt"] = "Press shortcut...",
            ["OptHotkeyF12"] = "F12 is reserved by Windows",
            ["OptHotkeyNeedMod"] = "Hold Ctrl, Alt, Shift or Win",
            ["OptHotkeyNone"] = "Disabled",
            ["HotkeyRegisterFailed"] = "Could not register {0}. Choose a different hotkey and apply it.",
            ["OptCooldown"] = "Post-reconnect Cooldown",
            ["OptTimeout"] = "Disconnect Timeout",
            ["OptEnableOverlay"] = "Show floating in-game overlay button",
            ["OptOverlayOnlyMatch"] = "Show overlay only when match endpoint is active",
            ["OptOverlayOnlyWhenFocused"] = "Show overlay only when Hearthstone is in focus",
            ["OptLockOverlay"] = "Lock overlay position (prevent right-drag)",
            ["OptMiniOverlay"] = "Use compact overlay button (R)",
            ["OverlayTestTooltip"] = "First click: preview for 20 seconds. Changes refresh the preview timer.\nSecond click: simulate a reconnect.\nClick again during the simulation to stop.",
            ["OptOverlayOpacity"] = "Overlay Opacity",
            ["OptOverlayBgOpacity"] = "Background Opacity",
            ["OptOverlayScale"] = "Overlay Scale",
            ["OptMinToTray"] = "Minimize to system tray on window close",
            ["OptMinToTrayOnMinimize"] = "Minimize to system tray when minimizing window",
            ["OptStartMin"] = "Start minimized to system tray",
            ["LangSwitchTooltipRu"] = "Переключить язык на русский",
            ["LangSwitchTooltipEn"] = "Switch language to English",
            ["UnitSeconds"] = "{0}s",
            ["OptLanguage"] = "Interface Language",
            ["OptHsPath"] = "Hearthstone Folder (auto if empty)",
            ["LangAuto"] = "Auto (System)",
            ["LangEn"] = "English",
            ["LangRu"] = "Русский",
            ["TrayShow"] = "Open HS-Reconnector",
            ["TrayReconnect"] = "Reconnect Now",
            ["TrayOverlay"] = "Floating Overlay",
            ["TrayExit"] = "Exit",
            ["TrayReady"] = "HS-Reconnector — Ready ({0})",
            ["TrayWaiting"] = "HS-Reconnector — Waiting for match",
            ["TrayDisconnected"] = "HS-Reconnector — Reconnecting...",
            ["OverlayTooltip"] = "Left-click: Reconnect\nRight-drag: Move button",
            ["OverlayTooltipLocked"] = "Left-click: Reconnect (position locked)",
            ["ElevationWarning"] = "Administrator privileges are required to reset Hearthstone TCP connections.",
            ["FolderDialogDesc"] = "Select the Hearthstone installation folder (containing Logs)"
        };

        private static readonly Dictionary<string, string> Ru = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AppTitle"] = "HS-Reconnector",
            ["BadgeAdmin"] = "Администратор",
            ["BadgeNoAdmin"] = "Без прав администратора",
            ["StatusCardTitle"] = "Состояние игры и подключения",
            ["LabelGame"] = "Hearthstone",
            ["LabelEndpoint"] = "Игровой сервер",
            ["LabelTcp"] = "TCP-сокет",
            ["LabelLog"] = "Активный лог",
            ["GameRunning"] = "Запущен",
            ["GameNotRunning"] = "Не запущен",
            ["EndpointWaiting"] = "Ожидание матча...",
            ["EndpointUnsupported"] = "Неподдерживаемый адрес (не IPv4)",
            ["OverlayUnsupported"] = "Только IPv4",
            ["TcpEstablished"] = "Установлено ({0})",
            ["TcpMultiple"] = "Несколько сокетов ({0})",
            ["TcpNone"] = "Нет активного соединения",
            ["TcpDisconnected"] = "Переподключение...",
            ["LogWaiting"] = "Лог не подключён",
            ["BtnReconnect"] = "Переподключиться",
            ["BtnDisconnected"] = "Отключено...",
            ["BtnWait"] = "Ждите {0}с",
            ["BtnClearLog"] = "Очистить",
            ["BtnSaveApply"] = "Сохранить и применить",
            ["BtnResetOverlay"] = "Сбросить позицию",
            ["BtnBrowse"] = "Обзор...",
            ["BtnResetHotkey"] = "Сброс",
            ["BtnClearHotkey"] = "Откл.",
            ["SectionHotkey"] = "Настройки реконнекта и оверлея",
            ["SectionBehavior"] = "Поведение и папка Hearthstone",
            ["SectionLogs"] = "Журнал событий",
            ["OptHotkey"] = "Горячая клавиша",
            ["OptHotkeyPrompt"] = "Нажмите сочетание...",
            ["OptHotkeyF12"] = "F12 зарезервирована Windows",
            ["OptHotkeyNeedMod"] = "Зажмите Ctrl, Alt, Shift или Win",
            ["OptHotkeyNone"] = "Отключено",
            ["HotkeyRegisterFailed"] = "Не удалось зарегистрировать {0}. Выберите другое сочетание и примените настройки.",
            ["OptCooldown"] = "Задержка после реконнекта",
            ["OptTimeout"] = "Таймаут сброса статуса",
            ["OptEnableOverlay"] = "Показывать кнопку поверх игры",
            ["OptOverlayOnlyMatch"] = "Показывать кнопку только во время матча",
            ["OptOverlayOnlyWhenFocused"] = "Показывать кнопку только когда Hearthstone в фокусе",
            ["OptLockOverlay"] = "Закрепить позицию кнопки (запретить перетаскивание)",
            ["OptMiniOverlay"] = "Использовать компактную кнопку (R)",
            ["OverlayTestTooltip"] = "Первое нажатие: показать кнопку на 20 секунд. Изменение настроек запускает отсчёт заново.\nВторое нажатие: имитация переподключения.\nНажмите ещё раз во время имитации, чтобы остановить её.",
            ["OptOverlayOpacity"] = "Прозрачность оверлея",
            ["OptOverlayBgOpacity"] = "Прозрачность фона",
            ["OptOverlayScale"] = "Масштаб оверлея",
            ["OptMinToTray"] = "Сворачивать в трей при закрытии окна",
            ["OptMinToTrayOnMinimize"] = "Сворачивать в трей при сворачивании окна",
            ["OptStartMin"] = "Запускать свёрнутым в трей",
            ["LangSwitchTooltipRu"] = "Переключить язык на русский",
            ["LangSwitchTooltipEn"] = "Switch language to English",
            ["UnitSeconds"] = "{0}с",
            ["OptLanguage"] = "Язык интерфейса",
            ["OptHsPath"] = "Папка Hearthstone (авто, если пусто)",
            ["LangAuto"] = "Авто (система)",
            ["LangEn"] = "English",
            ["LangRu"] = "Русский",
            ["TrayShow"] = "Открыть HS-Reconnector",
            ["TrayReconnect"] = "Переподключиться сейчас",
            ["TrayOverlay"] = "Плавающий оверлей",
            ["TrayExit"] = "Выход",
            ["TrayReady"] = "HS-Reconnector — Готов ({0})",
            ["TrayWaiting"] = "HS-Reconnector — Ожидание матча",
            ["TrayDisconnected"] = "HS-Reconnector — Переподключение...",
            ["OverlayTooltip"] = "ЛКМ: Переподключиться\nПКМ + перетаскивание: Переместить",
            ["OverlayTooltipLocked"] = "ЛКМ: Переподключиться (позиция закреплена)",
            ["ElevationWarning"] = "Для сброса TCP-соединения Hearthstone требуются права администратора.",
            ["FolderDialogDesc"] = "Выберите папку установки Hearthstone (где находится папка Logs)"
        };

        public static bool IsRussian
        {
            get
            {
                var lang = AppSettings.Instance.Language;
                if (string.Equals(lang, "ru", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase))
                    return false;
                return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
                    .Equals("ru", StringComparison.OrdinalIgnoreCase);
            }
        }

        public static string Get(string key)
        {
            var table = IsRussian ? Ru : En;
            if (table.TryGetValue(key, out var value))
                return value;
            return En.TryGetValue(key, out var fallback) ? fallback : key;
        }

        public static string Format(string key, params object[] args)
        {
            return string.Format(Get(key), args);
        }

        public static void NotifyChanged()
        {
            LanguageChanged?.Invoke();
        }
    }
}
