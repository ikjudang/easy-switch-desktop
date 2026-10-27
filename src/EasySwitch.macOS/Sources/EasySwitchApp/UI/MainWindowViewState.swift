import Foundation

/// Preserve a few navigation values, never a window, view or view model graph.
@MainActor
final class MainWindowViewState {
    var page: AppPage = .home
    var floatingExpanded = false
    var toastExpanded = false
    var exportExpanded = false
    var showLog = false
}
