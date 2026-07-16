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

/// AUTH-6: Mi account tokens are only ever stored encrypted. On the phone that is the Android
/// Keystore; here it is the Keychain, which is the same promise — the token never sits in plain
/// text on disk, and it is bound to this user on this Mac.
///
/// The sealed blob is a Keychain item, and what the store persists is only a reference to it. If
/// the Keychain refuses (a locked keychain, a corrupted item), we drop the session rather than
/// fall back to plaintext: the user simply signs in again, which is a far better outcome than a
/// token sitting readable in a plist.
final class KeychainSecretBox: NSObject, SecretBox {
    private let service = "com.bluzi.babymonitor"
    private let account = "mi-session"

    /// Which Keychain, decided by what the binary is actually entitled to — so one build works
    /// whether or not it was signed with a Developer ID.
    ///
    /// The **data-protection** Keychain keys on app identity, not on the bytes of the binary, so an
    /// update is still the same app and there is never a password prompt. It requires a
    /// `keychain-access-groups` entitlement, which only a Developer ID can carry — a self-signed
    /// certificate that claims it gets the process SIGKILLed at exec.
    ///
    /// The **login** Keychain is the fallback. It guards the item against the exact binary that
    /// wrote it, so each update costs one password prompt (AUTH-12).
    private static let accessGroup: String? = {
        guard let task = SecTaskCreateFromSelf(nil),
              let groups = SecTaskCopyValueForEntitlement(
                  task, "keychain-access-groups" as CFString, nil
              ) as? [String],
              let group = groups.first
        else {
            return nil
        }
        return group
    }()

    private var base: [String: Any] {
        var query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
        ]
        if let group = Self.accessGroup {
            query[kSecUseDataProtectionKeychain as String] = true
            query[kSecAttrAccessGroup as String] = group
        }
        return query
    }

    /// Returns nil if the Keychain refuses. **It must never throw**: an ObjC exception raised here
    /// unwinds through Kotlin and terminates the process — it did exactly that once, and a keychain
    /// that declined to store a token took the whole monitor down. AUTH-6 says drop the session; a
    /// nil says it in the one way that cannot be misunderstood across a language bridge.
    func seal(plain: String) -> String? {
        guard !Preview.active else { return nil } // the harness never touches a real Keychain
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
        guard !Preview.active else { return nil } // the harness never touches a real Keychain
        guard sealed == "keychain" else { return nil }
        var query = base
        query[kSecReturnData as String] = true
        query[kSecMatchLimit as String] = kSecMatchLimitOne
        var item: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &item)
        guard status == errSecSuccess,
              let data = item as? Data,
              let text = String(data: data, encoding: .utf8)
        else {
            // A refusal, a denial, a corrupted item — all the same answer: there is no session.
            // The user signs in again. Nothing crashes, and nothing falls back to plaintext.
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
