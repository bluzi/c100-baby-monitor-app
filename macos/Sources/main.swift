import AppKit

// DESK-1: the app lives in the menu bar, not the Dock. `.accessory` is what makes that true —
// no Dock icon, no app menu, and closing every window does not end the process (BG-5).
//
// The delegate is a top-level `let` on purpose. `NSApplication.delegate` is a WEAK reference, so a
// delegate held only by a local would be released the moment ARC decided it had no further uses —
// and the app would come up with no menu bar item, no window, and no explanation.
let appDelegate = MainActor.assumeIsolated { AppDelegate() }

let application = NSApplication.shared
application.setActivationPolicy(.accessory)
application.delegate = appDelegate
application.run()
