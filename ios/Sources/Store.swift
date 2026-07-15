import BabyMonitorCore
import Foundation
import Security

/// AUTH-5: where settings and the camera choice live. Not secrets — those go through SecretBox.
final class DefaultsStore: NSObject, KeyValueStore {
    private let defaults = UserDefaults.standard

    func get(key: String) -> String? { defaults.string(forKey: key) }
    func put(key: String, value: String) { defaults.set(value, forKey: key) }
    func remove(key: String) { defaults.removeObject(forKey: key) }
}

/// AUTH-6: Mi account tokens are only ever stored encrypted. On the phone that is the Keychain, the
/// same promise the Mac makes — the token never sits in plain text, and it is bound to this app on
/// this device.
///
/// On a real, provisioned device iOS is the easy case of the three: the Keychain keys items to the
/// app's identity (its provisioned keychain-access-group), so there is no password prompt and no
/// re-encryption across updates that the Mac has to work around (AUTH-6m). The **Simulator** is the
/// awkward one — the ad-hoc `swiftc` build carries no keychain-access-group and cannot be given one
/// (the Simulator's AMFI SIGKILLs an entitled ad-hoc build at exec; see ios/build.sh), so here
/// `SecItemAdd` returns `errSecMissingEntitlement` (-34018). On a device that is not hidden — `seal`
/// returns nil, the store drops the session, and AUTH-13 makes the app say sign-in could not be
/// completed rather than loop silently back to login. On the **Simulator** a dev-only UserDefaults
/// fallback (compiled out of every device build by `#if targetEnvironment(simulator)`) keeps the
/// login → cameras → viewer flow reachable instead — see `seal`.
/// What is *not* optional, on a device, is the accessibility class: `AfterFirstUnlock`, so a
/// background token refresh while the phone is locked (AUTH-7) can still read it — `WhenUnlocked`
/// would make the overnight refresh fail the moment the screen locked.
final class KeychainSecretBox: NSObject, SecretBox {
    private let service = "com.bluzi.babymonitor"
    private let account = "mi-session"

    #if targetEnvironment(simulator)
    /// DEV ONLY. The UserDefaults slot the Simulator fallback in `seal` uses in place of the
    /// (unavailable) Keychain. Never present in a device build.
    private static let simFallbackKey = "mi-session-sim-fallback"
    #endif

    private var base: [String: Any] {
        [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
        ]
    }

    /// Returns nil if the Keychain refuses. **It must never throw**: an exception raised here would
    /// unwind through Kotlin and terminate the process. AUTH-6 says drop the session on failure; a nil
    /// says exactly that across the language bridge, and the user simply signs in again — never a
    /// fallback to plaintext. (The lone exception is the Simulator dev fallback below, which is
    /// compiled out of every device build and so can never reach a phone.)
    func seal(plain: String) -> String? {
        guard !Preview.active else { return nil }
        var query = base
        SecItemDelete(query as CFDictionary)
        query[kSecValueData as String] = Data(plain.utf8)
        query[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlock
        let status = SecItemAdd(query as CFDictionary, nil)
        if status == errSecSuccess {
            // The Keychain holds the secret; the key-value store only needs to know it is there.
            return "keychain"
        }
        #if targetEnvironment(simulator)
        // DEV FALLBACK — SIMULATOR ONLY, compiled out of every device build by `#if`, so it can never
        // ship. The Simulator has no Keychain (the ad-hoc build cannot carry the entitlement, so
        // SecItemAdd returns -34018 — see build.sh), which would otherwise leave the app stuck at
        // login here forever (AUTH-13). Stash the session in UserDefaults so the login → cameras →
        // viewer flow can be exercised on the Simulator. This is plaintext, on a dev machine, in a
        // binary that never reaches a phone; on a real device the `#else` path drops it, as AUTH-6 says.
        Log.warn("data", "Keychain unavailable on the Simulator (OSStatus \(status)) — using the dev-only UserDefaults fallback so login can be tested")
        UserDefaults.standard.set(plain, forKey: Self.simFallbackKey)
        return "sim-fallback"
        #else
        Log.error("data", "keychain refused to store the session (OSStatus \(status))")
        return nil
        #endif
    }

    func open(sealed: String) -> String? {
        guard !Preview.active else { return nil }
        #if targetEnvironment(simulator)
        if sealed == "sim-fallback" { return UserDefaults.standard.string(forKey: Self.simFallbackKey) }
        #endif
        guard sealed == "keychain" else { return nil }
        var query = base
        query[kSecReturnData as String] = true
        query[kSecMatchLimit as String] = kSecMatchLimitOne
        var item: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &item)
        guard status == errSecSuccess, let data = item as? Data, let text = String(data: data, encoding: .utf8)
        else {
            if status != errSecItemNotFound {
                Log.warn("data", "keychain would not return the session (OSStatus \(status)) — signing in again")
            }
            return nil
        }
        return text
    }

    func clear() {
        guard !Preview.active else { return }
        SecItemDelete(base as CFDictionary)
        #if targetEnvironment(simulator)
        UserDefaults.standard.removeObject(forKey: Self.simFallbackKey)
        #endif
    }
}
