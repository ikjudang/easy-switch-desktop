import Foundation
import Security
import EasySwitchCore

enum RelaySecretStore {
    static func load() throws -> Data? {
        var result: CFTypeRef?
        let status = SecItemCopyMatching([kSecClass: kSecClassGenericPassword,
            kSecAttrService: "io.github.ikjudang.EasySwitch.relay", kSecAttrAccount: "pairing-key",
            kSecReturnData: true, kSecMatchLimit: kSecMatchLimitOne] as CFDictionary, &result)
        if status == errSecItemNotFound { return nil }
        guard status == errSecSuccess, let data = result as? Data, data.count == 32 else { throw NSError(domain: NSOSStatusErrorDomain, code: Int(status)) }
        return data
    }
    static func replace() throws -> Data {
        let secret = RelayProtocol.newSecret()
        let query = [kSecClass: kSecClassGenericPassword, kSecAttrService: "io.github.ikjudang.EasySwitch.relay", kSecAttrAccount: "pairing-key"] as [CFString: Any]
        let update = SecItemUpdate(query as CFDictionary, [kSecValueData: secret] as CFDictionary)
        let status = update == errSecItemNotFound
            ? SecItemAdd(query.merging([kSecValueData: secret, kSecAttrAccessible: kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly]) { _, new in new } as CFDictionary, nil)
            : update
        guard status == errSecSuccess else { throw NSError(domain: NSOSStatusErrorDomain, code: Int(status)) }
        return secret
    }
}
