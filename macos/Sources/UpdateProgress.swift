import AppKit

/// **UPD-9: the spinner a manual update check shows while it looks, and the one a download shows while
/// it runs.**
///
/// It is its own small floating panel rather than an `NSAlert`, because an alert's `runModal()` blocks
/// — and this has to be dismissed *by the app* the moment the check answers, not by the parent. The
/// Cancel button, when offered, stops the check in flight (UPD-8: cancelling touches nothing but the
/// check). A download shows the same panel with no Cancel: once the parent has said "install", it runs
/// to its end or fails loudly.
@MainActor
final class UpdateProgressPanel {
    private var panel: NSPanel?
    private var onCancel: (() -> Void)?

    func show(title: String, message: String, onCancel: (() -> Void)?) {
        close() // never two at once

        self.onCancel = onCancel

        let spinner = NSProgressIndicator()
        spinner.style = .spinning
        spinner.controlSize = .regular
        spinner.startAnimation(nil)
        spinner.setContentHuggingPriority(.required, for: .horizontal)

        let titleLabel = NSTextField(labelWithString: title)
        titleLabel.font = .boldSystemFont(ofSize: 13)

        let messageLabel = NSTextField(wrappingLabelWithString: message)
        messageLabel.textColor = .secondaryLabelColor
        messageLabel.preferredMaxLayoutWidth = 260

        let text = NSStackView(views: [titleLabel, messageLabel])
        text.orientation = .vertical
        text.alignment = .leading
        text.spacing = 3

        let top = NSStackView(views: [spinner, text])
        top.orientation = .horizontal
        top.alignment = .centerY
        top.spacing = 14

        let content = NSStackView(views: [top])
        content.orientation = .vertical
        content.alignment = .trailing
        content.spacing = 18
        content.edgeInsets = NSEdgeInsets(top: 22, left: 24, bottom: 22, right: 24)

        if onCancel != nil {
            let cancel = NSButton(title: "Cancel", target: self, action: #selector(cancelClicked))
            cancel.bezelStyle = .rounded
            cancel.keyEquivalent = "\u{1b}" // Escape
            content.addArrangedSubview(cancel)
        }

        let panel = NSPanel(
            contentRect: NSRect(x: 0, y: 0, width: 340, height: 120),
            styleMask: [.titled, .fullSizeContentView],
            backing: .buffered,
            defer: false
        )
        panel.titleVisibility = .hidden
        panel.titlebarAppearsTransparent = true
        panel.isMovableByWindowBackground = true
        panel.isFloatingPanel = true
        panel.hidesOnDeactivate = false
        panel.contentView = content
        panel.setContentSize(content.fittingSize)
        // No traffic lights: the panel is dismissed by the app when the work answers, or by Cancel —
        // never by a close button that would orphan the check still running behind it.
        panel.standardWindowButton(.closeButton)?.isHidden = true
        panel.standardWindowButton(.miniaturizeButton)?.isHidden = true
        panel.standardWindowButton(.zoomButton)?.isHidden = true
        panel.center()
        panel.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
        self.panel = panel
    }

    func close() {
        panel?.orderOut(nil)
        panel?.close()
        panel = nil
        onCancel = nil
    }

    @objc private func cancelClicked() {
        onCancel?()
    }
}
