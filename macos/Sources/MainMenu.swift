import AppKit

/// MACOS-13. **The Edit menu is not decoration — it is how ⌘V works.**
///
/// This app had no main menu at all. On a Mac, ⌘V is not handled by the text field: the keystroke
/// is offered to the main menu, and it is the Edit menu's Paste item that turns it into a
/// `paste:` down the responder chain. With no menu, there is no Paste item, and ⌘V in the password
/// field did *nothing* — silently, with no error, in the one place where a parent is most likely to
/// be pasting from a password manager. That was the bug.
///
/// So: the standard menus, in the standard order, with the standard shortcuts. Nothing clever. A
/// Mac app that behaves like a Mac app is the whole of Apple's standard, and most of it is simply
/// this — giving the system its own furniture back rather than reinventing it.
@MainActor
enum MainMenu {
    static func install() {
        let main = NSMenu()
        main.addItem(appMenu())
        main.addItem(editMenu())
        main.addItem(viewMenu())
        main.addItem(windowMenu())
        NSApp.mainMenu = main
    }

    private static func appMenu() -> NSMenuItem {
        let item = NSMenuItem()
        let menu = NSMenu(title: "Baby Monitor")

        menu.addItem(withTitle: "About Baby Monitor", action: #selector(AppDelegate.showAbout(_:)), keyEquivalent: "")
        menu.addItem(.separator())
        menu.addItem(withTitle: "Settings…", action: #selector(AppDelegate.showSettings(_:)), keyEquivalent: ",")
        menu.addItem(withTitle: "Check for Updates…", action: #selector(AppDelegate.checkForUpdatesNow(_:)), keyEquivalent: "")
        menu.addItem(.separator())

        let services = NSMenu(title: "Services")
        let servicesItem = NSMenuItem(title: "Services", action: nil, keyEquivalent: "")
        servicesItem.submenu = services
        NSApp.servicesMenu = services
        menu.addItem(servicesItem)
        menu.addItem(.separator())

        menu.addItem(withTitle: "Hide Baby Monitor", action: #selector(NSApplication.hide(_:)), keyEquivalent: "h")
        let hideOthers = NSMenuItem(
            title: "Hide Others",
            action: #selector(NSApplication.hideOtherApplications(_:)),
            keyEquivalent: "h"
        )
        hideOthers.keyEquivalentModifierMask = [.command, .option]
        menu.addItem(hideOthers)
        menu.addItem(withTitle: "Show All", action: #selector(NSApplication.unhideAllApplications(_:)), keyEquivalent: "")
        menu.addItem(.separator())

        // MACOS-9: the only thing that ends the app. Closing a window never does.
        menu.addItem(withTitle: "Quit Baby Monitor", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")

        item.submenu = menu
        return item
    }

    /// The one that matters (MACOS-13). Standard selectors, standard shortcuts: every text field in
    /// the app — and every one it ever grows — gets Cut, Copy, Paste and Select All for free,
    /// because that is how AppKit was always meant to be wired.
    private static func editMenu() -> NSMenuItem {
        let item = NSMenuItem()
        let menu = NSMenu(title: "Edit")

        menu.addItem(withTitle: "Undo", action: Selector(("undo:")), keyEquivalent: "z")
        menu.addItem(withTitle: "Redo", action: Selector(("redo:")), keyEquivalent: "Z")
        menu.addItem(.separator())
        menu.addItem(withTitle: "Cut", action: #selector(NSText.cut(_:)), keyEquivalent: "x")
        menu.addItem(withTitle: "Copy", action: #selector(NSText.copy(_:)), keyEquivalent: "c")
        menu.addItem(withTitle: "Paste", action: #selector(NSText.paste(_:)), keyEquivalent: "v")
        menu.addItem(withTitle: "Delete", action: #selector(NSText.delete(_:)), keyEquivalent: "")
        menu.addItem(.separator())
        menu.addItem(withTitle: "Select All", action: #selector(NSText.selectAll(_:)), keyEquivalent: "a")

        item.submenu = menu
        return item
    }

    /// MACOS-6: the full shape can go full screen. AppKit renames this as it toggles and disables it
    /// by itself while the window on screen is the tile — which cannot be full screen — so the menu
    /// never offers something that would do nothing.
    private static func viewMenu() -> NSMenuItem {
        let item = NSMenuItem()
        let menu = NSMenu(title: "View")

        let fullScreen = NSMenuItem(
            title: "Enter Full Screen",
            action: #selector(NSWindow.toggleFullScreen(_:)),
            keyEquivalent: "f"
        )
        fullScreen.keyEquivalentModifierMask = [.control, .command]
        menu.addItem(fullScreen)

        item.submenu = menu
        return item
    }

    private static func windowMenu() -> NSMenuItem {
        let item = NSMenuItem()
        let menu = NSMenu(title: "Window")

        menu.addItem(withTitle: "Minimize", action: #selector(NSWindow.performMiniaturize(_:)), keyEquivalent: "m")
        menu.addItem(withTitle: "Zoom", action: #selector(NSWindow.performZoom(_:)), keyEquivalent: "")
        menu.addItem(.separator())

        menu.addItem(withTitle: "Show Camera", action: #selector(AppDelegate.showMonitorWindow(_:)), keyEquivalent: "0")

        // MACOS-14: the same window, worn small. ⇧⌘M is where a Mac user already reaches for a
        // mini player, so it is where this is too.
        let mini = NSMenuItem(
            title: "Mini Window",
            action: #selector(AppDelegate.toggleShape(_:)),
            keyEquivalent: "M"
        )
        menu.addItem(mini)
        menu.addItem(.separator())

        menu.addItem(withTitle: "Close", action: #selector(NSWindow.performClose(_:)), keyEquivalent: "w")
        menu.addItem(.separator())
        menu.addItem(withTitle: "Bring All to Front", action: #selector(NSApplication.arrangeInFront(_:)), keyEquivalent: "")

        item.submenu = menu
        NSApp.windowsMenu = menu
        return item
    }
}
