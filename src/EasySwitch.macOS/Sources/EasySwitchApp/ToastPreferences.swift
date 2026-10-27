import Foundation

struct ToastPreferences: Equatable {
    let enabled: Bool
    let size: Int
    let transparency: Int
    var scale: CGFloat { CGFloat(size) / 100 }
    var opacity: CGFloat { 1 - CGFloat(transparency) / 100 }

    init(enabled: Bool = true, size: Int = 100, transparency: Int = 0) {
        self.enabled = enabled
        self.size = min(200, max(50, size))
        self.transparency = min(100, max(0, transparency))
    }

    init(defaults: UserDefaults) {
        self.init(enabled: defaults.object(forKey: "toastEnabled").map { _ in defaults.bool(forKey: "toastEnabled") } ?? true,
                  size: defaults.object(forKey: "toastSize").map { _ in defaults.integer(forKey: "toastSize") } ?? 100,
                  transparency: defaults.integer(forKey: "toastTransparency"))
    }

    func save(to defaults: UserDefaults) {
        defaults.set(enabled, forKey: "toastEnabled")
        defaults.set(size, forKey: "toastSize")
        defaults.set(transparency, forKey: "toastTransparency")
    }
}
