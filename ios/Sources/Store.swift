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
/// iOS is the easy case of the three. The Keychain keys items to the app's identity (the bundle id /
/// its default access group), so there is no password prompt and no re-encryption across updates that
/// the Mac has to work around (AUTH-6m); and there is no access-group entitlement to arrange, because
/// the app's own group is implicit. What is *not* optional is the accessibility class:
/// `AfterFirstUnlock`, so a background token refresh while the phone is locked (AUTH-7) can still read
/// it — `WhenUnlocked` would make the overnight refresh fail the moment the screen locked.
final class KeychainSecretBox: NSObject, SecretBox {
    private let service = "com.bluzi.babymonitor"
    private let account = "mi-session"

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
    /// fallback to plaintext.
    func seal(plain: String) -> String? {
        guard !Preview.active else { return nil }
        var query = base
        SecItemDelete(query as CFDictionary)
        query[kSecValueData as String] = Data(plain.utf8)
        query[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlock
        let status = SecItemAdd(query as CFDictionary, nil)
        guard status == errSecSuccess else {
            Log.error("data", "keychain refused to store the session (OSStatus \(status))")
            return nil
        }
        // The Keychain holds the secret; the key-value store only needs to know it is there.
        return "keychain"
    }

    func open(sealed: String) -> String? {
        guard !Preview.active else { return nil }
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
    }
}
