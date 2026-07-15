import Foundation
import os

/// One `os_log` subsystem, per-subsystem tags in the message — the same shape as the phone's logcat
/// and the Mac's unified log, so a night can be reconstructed the same way on any device.
///
/// Read it live:  log stream --predicate 'subsystem == "com.bluzi.babymonitor"'
enum Log {
    private static let logger = Logger(subsystem: "com.bluzi.babymonitor", category: "BabyMonitor")
    private static let started = Date()

    static func debug(_ tag: String, _ message: String) { emit("D", tag, message); logger.debug("[\(tag)] \(message)") }
    static func info(_ tag: String, _ message: String) { emit("I", tag, message); logger.info("[\(tag)] \(message)") }
    static func warn(_ tag: String, _ message: String) { emit("W", tag, message); logger.warning("[\(tag)] \(message)") }
    static func error(_ tag: String, _ message: String) { emit("E", tag, message); logger.error("[\(tag)] \(message)") }

    /// Also to stderr, which `simctl launch --console` surfaces in the terminal — the unified log is
    /// fine for a shipped app but awkward to read back from an ad-hoc build under the simulator.
    private static func emit(_ level: String, _ tag: String, _ message: String) {
        let t = String(format: "%7.2f", Date().timeIntervalSince(started))
        FileHandle.standardError.write("\(t) \(level) [\(tag)] \(message)\n".data(using: .utf8)!)
    }

    /// The sink core logs through, so the protocol layer's logs land in the same place.
    static func sink(level: String, tag: String, message: String) {
        switch level {
        case "DEBUG": debug(tag, message)
        case "WARN": warn(tag, message)
        case "ERROR": error(tag, message)
        default: info(tag, message)
        }
    }
}
