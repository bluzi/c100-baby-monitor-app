import AppKit

/// **Every question the app asks, in one place.**
///
/// They live here rather than inline for the same reason the specs exist: these four sentences are
/// the whole of what a half-asleep parent is told at the two moments the app can hurt them — when
/// something is about to stop watching their baby, and when something is about to replace the code
/// that is watching. They are worth being able to read all at once, and worth being able to *look*
/// at without a camera, a Mi account and a published release (see `Preview`, `BM_UI_ALERT`).
@MainActor
enum Alerts {
    /// **BG-14 / DESK-3: quitting is how a Mac stops monitoring, so quitting asks first.**
    ///
    /// The phone guards its Stop button with a confirmation because a single stray tap must never end
    /// a watch. On a Mac that weight sits on Quit — and the wording has to earn it: not "are you
    /// sure?", which is a question nobody reads, but what actually happens, and to whom.
    static func quit() -> NSAlert {
        let alert = NSAlert()
        alert.messageText = "Quit Baby Monitor?"
        alert.informativeText = "Quitting stops monitoring: audio, the alarm and the connection all "
            + "end. The baby will not be monitored until you open it again."
        alert.addButton(withTitle: "Quit and Stop Monitoring")
        alert.addButton(withTitle: "Keep Monitoring")
        alert.alertStyle = .warning
        return alert
    }

    /// **UPD-5: the app never restarts itself — it asks, once, whether to install and restart now.**
    ///
    /// The question names both versions — what is running now and what is on offer — so the parent
    /// knows exactly what changes. Declining costs nothing: nothing is downloaded and nothing changes.
    /// That is why it is worded as an offer rather than a warning — and why, when a monitor *is*
    /// running, it says out loud what a restart costs: seconds of not watching, with the parent
    /// standing right there.
    static func updateAvailable(current: String, version: String, monitoring: Bool) -> NSAlert {
        let alert = NSAlert()
        alert.messageText = "An update is available"
        let lead = "Baby Monitor \(version) is available — you’re running \(current). "
        alert.informativeText = monitoring
            ? lead + "Installing it restarts the app: monitoring stops for a few seconds while it does, "
                + "then resumes by itself."
            : lead + "Installing it downloads it and reopens on the new version."
        alert.addButton(withTitle: "Install and Restart")
        alert.addButton(withTitle: "Not Now")
        alert.alertStyle = .informational
        return alert
    }

    /// UPD-9: a check asked for by hand answers, whatever the answer is. A click that produces silence
    /// is a click the user has to wonder about.
    static func plain(title: String, body: String) -> NSAlert {
        let alert = NSAlert()
        alert.messageText = title
        alert.informativeText = body
        alert.addButton(withTitle: "OK")
        return alert
    }
}
