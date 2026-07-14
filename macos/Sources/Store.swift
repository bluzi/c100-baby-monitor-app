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

    /// Returns nil if the Keychain refuses. **It must never throw**: an ObjC exception raised here
    /// unwinds through Kotlin and terminates the process — it did exactly that once, and a keychain
    /// that declined to store a token took the whole monitor down. AUTH-6 says drop the session; a
    /// nil says it in the one way that cannot be misunderstood across a language bridge.
    func seal(plain: String) -> String? {
        var query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
        ]
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
        guard sealed == "keychain" else { return nil }
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne,
        ]
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
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
        ]
        SecItemDelete(query as CFDictionary)
    }
}

/// A separate Keychain slot for the updater's GitHub token (UPD-3). It is not the Mi session and
/// must not share its fate: signing out of Xiaomi should not stop the app updating itself.
enum UpdaterToken {
    private static let service = "com.bluzi.babymonitor.updater"
    private static let account = "github-token"

    static func load() -> String? {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne,
        ]
        var item: CFTypeRef?
        guard SecItemCopyMatching(query as CFDictionary, &item) == errSecSuccess,
              let data = item as? Data
        else {
            return nil
        }
        return String(data: data, encoding: .utf8)
    }

    static func save(_ token: String) {
        var query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
        ]
        SecItemDelete(query as CFDictionary)
        query[kSecValueData as String] = Data(token.utf8)
        query[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlock
        SecItemAdd(query as CFDictionary, nil)
    }

    static func clear() {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
        ]
        SecItemDelete(query as CFDictionary)
    }
}
