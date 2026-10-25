import AppKit
import SwiftUI

enum AppPage: String, CaseIterable {
    case home, devices, settings, logs

    var title: String {
        switch self {
        case .home: "EasySwitch"
        case .devices: "기기 선택"
        case .settings: "설정"
        case .logs: "연결 로그"
        }
    }
}

@MainActor
struct AppRootView: View {
    @ObservedObject var model: AppModel
    @State private var page: AppPage
    @State private var editingChannel: Int?
    private let settingsExpanded: Bool
    private let exportExpanded: Bool

    init(model: AppModel, initialPage: AppPage = .home, settingsExpanded: Bool = false, exportExpanded: Bool = false) {
        self.settingsExpanded = settingsExpanded; self.exportExpanded = exportExpanded
        self.model = model
        _page = State(initialValue: initialPage)
    }

    var body: some View {
        HStack(spacing: 0) {
            rail
            VStack(alignment: .leading, spacing: 0) {
                pageHeader
                switch page {
                case .home: home
                case .devices: devices
                case .settings, .logs:
                    AppSettingsView(model: model, floatingExpanded: settingsExpanded, exportExpanded: exportExpanded, showLog: page == .logs)
                }
            }
            .padding(.horizontal, 32)
            .padding(.top, 30)
            .padding(.bottom, 30)
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        }
        .background(AppTheme.Bg)
        .foregroundStyle(AppTheme.Text)
        .font(AppTheme.body())
        .preferredColorScheme(.dark)
        .frame(minWidth: 860, minHeight: 558)
        .onReceive(model.pageRequests) { page = $0 }
    }

    private var rail: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 11) {
                AppBrandMark()
                Text("EasySwitch").font(AppTheme.body(15, weight: .heavy))
            }
            .padding(.leading, 6)
            .padding(.bottom, 34)
            ForEach([AppPage.home, .devices, .settings], id: \.self) { destination in
                let active = page == destination || (destination == .settings && page == .logs)
                Button { page = destination } label: {
                    HStack(spacing: 12) {
                        AppGlyph(kind: destination == .home ? "home" : destination == .devices ? "devices" : "gear",
                                 size: 18, tint: active ? AppTheme.PointAccent : AppTheme.Text2)
                        Text(destination == .home ? t("홈", "Home") : pageTitle(destination))
                            .font(AppTheme.body(13.5, weight: active ? .bold : .semibold))
                            .foregroundStyle(active ? AppTheme.PointTintText : AppTheme.Text2)
                        Spacer(minLength: 0)
                    }
                    .padding(.horizontal, 12).frame(height: 40)
                    .background(active ? AppTheme.PointTintBg : .clear,
                                in: RoundedRectangle(cornerRadius: 12))
                }
                .buttonStyle(AppActionStyle(radius: 12))
                .padding(.bottom, 6)
            }
            Spacer()
            VStack(alignment: .leading, spacing: 8) {
                HStack(spacing: 8) {
                    Circle().fill(model.followEnabled ? AppTheme.Success : AppTheme.Warn).frame(width: 6, height: 6)
                    Text(model.followEnabled ? t("마우스 따라가기 켜짐", "Mouse follow on") : t("마우스 따라가기 꺼짐", "Mouse follow off"))
                        .font(AppTheme.body(11)).foregroundStyle(AppTheme.Text2)
                }
                Text("Easy Switch v0.1.6")
                    .font(AppTheme.mono(11)).foregroundStyle(AppTheme.GlyphDisabled)
            }
            .padding(.leading, 6)
        }
        .padding(.horizontal, 16).padding(.top, 26).padding(.bottom, 24)
        .frame(width: 200)
        .frame(maxHeight: .infinity)
        .background(AppTheme.BgRail)
    }

    private var pageHeader: some View {
        HStack(alignment: .center) {
            VStack(alignment: .leading, spacing: 3) {
                Text(pageTitle(page)).font(AppTheme.body(26, weight: .heavy))
                if page == .devices {
                    Text(t("함께 전환할 기기를 선택해 주세요.", "Choose devices to switch together."))
                        .font(AppTheme.body(13)).foregroundStyle(AppTheme.Text2)
                }
            }
            Spacer()
            if page == .devices {
                Button { model.refresh() } label: {
                    Group {
                        if model.scanInProgress { ProgressView().controlSize(.small).tint(AppTheme.Text2) }
                        else { AppGlyph(kind: "refresh") }
                    }
                    .frame(width: 38, height: 38)
                    .background(AppTheme.Card, in: RoundedRectangle(cornerRadius: 16))
                }
                .buttonStyle(AppActionStyle(radius: 16))
                .disabled(model.scanInProgress)
                .help(t("앱이 연결된 기기를 다시 조회해요.", "EasySwitch checks connected devices again."))
                .accessibilityLabel(t("기기 새로고침", "Refresh devices"))
            }
        }
        .frame(maxWidth: page == .devices ? 426 : .infinity)
        .padding(.bottom, page == .devices ? 22 : 24)
    }

    private var selectedDevices: [DeviceViewState] {
        model.devices.filter { $0.id == model.selectedKeyboardID || $0.id == model.selectedMouseID }
    }

    private var currentHost: Int? {
        let online = selectedDevices.filter(\.online)
        guard !online.isEmpty, let first = online.first?.currentHost,
              online.allSatisfy({ $0.currentHost == first }) else { return nil }
        return first
    }

    private var home: some View {
        GeometryReader { geometry in
            let deviceWidth: CGFloat = geometry.size.width >= 800 ? 300 : 252
            HStack(alignment: .top, spacing: 24) {
                VStack(alignment: .leading, spacing: 12) {
                    HStack(spacing: 0) {
                        Text(t("연결된 기기 (\(selectedDevices.filter(\.online).count)대)", "Connected devices (\(selectedDevices.filter(\.online).count))"))
                            .font(AppTheme.body(12, weight: .semibold)).foregroundStyle(AppTheme.Text2)
                        Spacer(minLength: 0)
                        Button { page = .devices } label: {
                            Text(t("기기 선택", "Select")).font(AppTheme.body(12, weight: .semibold))
                                .foregroundStyle(AppTheme.PointAccent).padding(.horizontal, 8).frame(height: 28)
                        }
                        .buttonStyle(AppActionStyle(radius: 8))
                    }
                    .frame(height: 28)
                    if selectedDevices.isEmpty {
                        AppCard {
                            VStack(alignment: .leading, spacing: 9) {
                                Text(t("선택한 기기가 없어요", "No devices selected")).font(AppTheme.body(14, weight: .bold))
                                Text(t("기기 선택에서 키보드와 마우스를 선택해 주세요.", "Choose a keyboard and mouse in Select devices."))
                                    .font(AppTheme.body(12)).foregroundStyle(AppTheme.Text2)
                                    .fixedSize(horizontal: false, vertical: true)
                            }
                            .padding(18).frame(maxWidth: .infinity, alignment: .leading)
                        }
                    } else {
                        VStack(spacing: 9) {
                            ForEach(selectedDevices) { device in homeDeviceCard(device) }
                        }
                    }
                }
                .frame(width: deviceWidth)
                VStack(alignment: .leading, spacing: 12) {
                    Text(t("전환할 채널", "Switch channel")).font(AppTheme.body(12, weight: .semibold))
                        .foregroundStyle(AppTheme.TextMuted).frame(height: 28)
                    HStack(spacing: 14) {
                        ForEach(1...3, id: \.self) { channel in channelCard(channel) }
                    }
                }
                .frame(maxWidth: .infinity)
            }
        }
    }

    private func homeDeviceCard(_ device: DeviceViewState) -> some View {
        AppCard(radius: 14) {
            HStack(spacing: 11) {
                iconTile(device.kind == "키보드" ? "keyboard" : "mouse", active: device.online, size: 32)
                VStack(alignment: .leading, spacing: 6) {
                    Text(device.name).font(AppTheme.body(13.5, weight: .bold)).lineLimit(1)
                    HStack(spacing: 6) {
                        Circle().fill(device.online ? AppTheme.Success : AppTheme.NumeralIdle).frame(width: 6, height: 6)
                        Text(device.online
                             ? device.kind == "키보드" ? (model.followEnabled ? t("키보드: 전환 감지 중", "Keyboard: detecting switches") : t("키보드: 연결됨", "Keyboard: connected")) : t("마우스: 전환 준비됨", "Mouse: ready to switch")
                             : t("연결 안 됨", "Not connected"))
                            .font(AppTheme.body(11)).foregroundStyle(AppTheme.Text2).lineLimit(1)
                    }
                }
                Spacer(minLength: 0)
            }
            .padding(.horizontal, 18).frame(height: 72)
            .opacity(device.online ? 1 : 0.6)
        }
    }

    private func channelCard(_ channel: Int) -> some View {
        let active = currentHost == channel - 1
        let available = selectedDevices.contains { $0.online && $0.hostCount >= channel && $0.featureIndex > 0 }
        return Button { if editingChannel == nil { model.switchChannel(channel) } } label: {
            VStack(spacing: 0) {
                Text(String(channel)).font(AppTheme.mono(44, bold: true))
                    .foregroundStyle(active ? AppTheme.Text : AppTheme.NumeralIdle)
                    .frame(height: 60).padding(.top, 12)
                Text(model.channelLabel(channel)).font(AppTheme.body(14, weight: .bold)).lineLimit(1).padding(.top, 8)
                Group {
                    if active {
                        HStack(spacing: 5) {
                            Circle().fill(AppTheme.Text).frame(width: 5, height: 5)
                            Text(t("현재 기기 연결 중", "Devices connected")).font(AppTheme.body(9, weight: .bold)).lineLimit(1)
                        }
                        .padding(.horizontal, 9).padding(.vertical, 4)
                        .background(Color.white.opacity(0.2), in: Capsule())
                    } else {
                        Text(t("눌러서 전환", "Click to switch")).font(AppTheme.body(11)).foregroundStyle(AppTheme.TextMuted)
                    }
                }
                .frame(height: 22).padding(.top, 12)
                Spacer(minLength: 12)
            }
            .frame(maxWidth: .infinity).frame(height: 154)
            .background(active ? AppTheme.Point : AppTheme.Card,
                        in: RoundedRectangle(cornerRadius: 20))
            .overlay(RoundedRectangle(cornerRadius: 20)
                .strokeBorder(active ? Color.clear : AppTheme.Border, lineWidth: 1))
        }
        .buttonStyle(AppActionStyle(radius: 20))
        .disabled(!available)
        .accessibilityLabel(t("선택한 기기를 채널 \(channel)로 전환", "Switch selected devices to channel \(channel)"))
        .overlay(SecondaryClickCapture { editingChannel = channel })
        .popover(isPresented: Binding(get: { editingChannel == channel }, set: { if !$0 { editingChannel = nil } })) {
            ChannelNameEditor(model: model, channel: channel) { editingChannel = nil }
        }
    }

    private var devices: some View {
        VStack(alignment: .leading, spacing: 0) {
            if model.needsInputMonitoring {
                inputMonitoringCard.padding(.bottom, 16)
            }
            ScrollView(.vertical) {
                VStack(spacing: 10) {
                    if model.devices.isEmpty && !model.needsInputMonitoring {
                        AppCard {
                            VStack(alignment: .leading, spacing: 9) {
                                Text(model.scanInProgress ? t("앱이 기기를 찾고 있어요", "EasySwitch is finding devices") : t("연결된 기기가 없어요", "No connected devices"))
                                    .font(AppTheme.body(14, weight: .bold))
                                Text(t("키보드와 마우스를 이 Mac에 연결해 주세요.", "Connect your keyboard and mouse to this Mac."))
                                    .font(AppTheme.body(12)).foregroundStyle(AppTheme.Text2)
                            }
                            .padding(20).frame(maxWidth: .infinity, alignment: .leading)
                        }
                    }
                    ForEach(model.devices) { device in selectableDeviceCard(device) }
                }
                .padding(.trailing, 2)
            }
            .frame(maxHeight: model.devices.isEmpty && model.needsInputMonitoring
                   ? 0 : CGFloat(max(model.devices.count, 1) * 80 + 8), alignment: .top)
            Button { page = .home } label: {
                Text(t("\(selectedDevices.count)개 선택 완료", "Continue with \(selectedDevices.count) selected"))
                    .font(AppTheme.body(16, weight: .bold)).frame(maxWidth: .infinity).frame(height: 54)
            }
            .buttonStyle(AppActionStyle(prominent: true, radius: 18))
            .padding(.top, 20)
            Spacer(minLength: 0)
        }
        .frame(maxWidth: 426)
    }

    private var inputMonitoringCard: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(spacing: 10) {
                AppGlyph(kind: "keyboard", size: 20, tint: AppTheme.Warn)
                Text(t("기기 접근 권한", "Device access permission")).font(AppTheme.body(15, weight: .bold))
                    .foregroundStyle(AppTheme.WarnText)
            }
            Text(t("EasySwitch가 전환 신호를 읽으려면 입력 모니터링 허용이 필요해요. 사용자가 macOS 설정에서 EasySwitch를 허용한 뒤 앱을 다시 시작해 주세요.", "EasySwitch needs Input Monitoring permission to read switch signals. Allow EasySwitch in macOS Settings, then restart the app."))
                .font(AppTheme.body(12)).foregroundStyle(AppTheme.WarnBody)
                .fixedSize(horizontal: false, vertical: true)
            HStack(spacing: 10) {
                Button { model.requestInputMonitoring() } label: {
                    Text(t("권한 설정 열기", "Open permissions")).font(AppTheme.body(12, weight: .semibold))
                        .foregroundStyle(AppTheme.WarnText)
                        .padding(.horizontal, 12).frame(height: 34)
                }
                .buttonStyle(AppActionStyle(outlined: true))
                Button { model.reopenHID() } label: {
                    Text(t("기기 다시 확인", "Check devices again")).font(AppTheme.body(12, weight: .semibold))
                        .foregroundStyle(AppTheme.WarnText)
                        .padding(.horizontal, 12).frame(height: 34)
                }
                .buttonStyle(AppActionStyle(outlined: true))
                .disabled(model.scanInProgress)
            }
        }
        .padding(18).frame(maxWidth: .infinity, alignment: .leading)
        .background(AppTheme.WarnCardBg, in: RoundedRectangle(cornerRadius: 16))
        .overlay(RoundedRectangle(cornerRadius: 16).strokeBorder(AppTheme.WarnBorder, lineWidth: 1))
        .fixedSize(horizontal: false, vertical: true)
    }

    private func selectableDeviceCard(_ device: DeviceViewState) -> some View {
        let selected = device.id == model.selectedKeyboardID || device.id == model.selectedMouseID
        let recognized = device.kind == "키보드" || device.kind == "마우스"
        return Button {
            if device.kind == "키보드" { model.selectKeyboard(selected ? nil : device.id) }
            else if device.kind == "마우스" { model.selectMouse(selected ? nil : device.id) }
        } label: {
            AppCard(selected: selected) {
                HStack(spacing: 20) {
                    RoundedRectangle(cornerRadius: 7)
                        .fill(selected ? AppTheme.Point : Color.clear)
                        .overlay(RoundedRectangle(cornerRadius: 7).strokeBorder(selected ? AppTheme.Point : AppTheme.BorderStrong, lineWidth: 2))
                        .overlay { if selected { AppGlyph(kind: "check", size: 15, tint: .white) } }
                        .frame(width: 22, height: 22)
                    HStack(spacing: 8) {
                        iconTile(device.kind == "키보드" ? "keyboard" : "mouse", active: selected, size: 34)
                        VStack(alignment: .leading, spacing: 5) {
                            Text(device.name).font(AppTheme.body(15, weight: .bold)).lineLimit(1)
                            HStack(spacing: 6) {
                                Circle().fill(device.online ? AppTheme.Success : AppTheme.NumeralIdle).frame(width: 6, height: 6)
                                Text("\(device.online ? t("연결됨", "Connected") : t("연결 안 됨", "Not connected")) · \(kindLabel(device.kind)) · \(device.connection)")
                                    .font(AppTheme.body(11)).foregroundStyle(AppTheme.Text2).lineLimit(1)
                            }
                        }
                        Spacer(minLength: 0)
                    }
                }
                .padding(.horizontal, 15).frame(height: 70)
            }
        }
        .buttonStyle(AppActionStyle(radius: 16))
        .disabled(!recognized && !selected)
        .opacity(device.online ? 1 : 0.6)
        .accessibilityLabel("\(device.name), \(kindLabel(device.kind)), \(selected ? t("선택됨", "Selected") : t("선택 안 됨", "Not selected"))")
    }

    private func t(_ ko: String, _ en: String) -> String { model.t(ko, en) }

    private func pageTitle(_ destination: AppPage) -> String {
        switch destination {
        case .home: return "EasySwitch"
        case .devices: return t("기기 선택", "Select devices")
        case .settings, .logs: return t("설정", "Settings")
        }
    }

    private func kindLabel(_ kind: String) -> String {
        kind == "키보드" ? t("키보드", "Keyboard") : kind == "마우스" ? t("마우스", "Mouse") : t("기기", "Device")
    }

    private func iconTile(_ kind: String, active: Bool = false, size: CGFloat = 40) -> some View {
        RoundedRectangle(cornerRadius: 12)
            .fill(active ? AppTheme.PointTintIconBg : AppTheme.Inset)
            .overlay(AppGlyph(kind: kind, size: 20, tint: active ? AppTheme.PointTintText : AppTheme.Text2))
            .frame(width: size, height: size)
    }
}

private extension AppTheme {
    // Existing shared warning tokens from the macOS handoff design-tokens.json.
    static let WarnText = Color(hex: 0xF2CF76)
    static let WarnBody = Color(hex: 0xC9B98A)
    static let WarnCardBg = Color(hex: 0x2A2210)
    static let WarnBorder = Color(hex: 0x6B5A1E)
}

/// A native scroll view reserves its gutter, including when the log is short.
struct LogTextView: NSViewRepresentable {
    let lines: [String]

    func makeNSView(context: Context) -> NSScrollView {
        let scroll = StableGutterScrollView(contentPadding: 12, scrollerVerticalPadding: 3)
        scroll.drawsBackground = true
        scroll.backgroundColor = NSColor(AppTheme.LogBg)
        let text = NSTextView()
        text.isEditable = false
        text.isSelectable = true
        text.drawsBackground = false
        text.font = NSFont(name: "SpaceMono-Regular", size: 11) ?? .monospacedSystemFont(ofSize: 11, weight: .regular)
        text.textColor = NSColor(AppTheme.TextBody)
        text.textContainerInset = .zero
        text.isVerticallyResizable = true
        text.isHorizontallyResizable = false
        text.autoresizingMask = [.width]
        text.textContainer?.widthTracksTextView = true
        text.textContainer?.lineFragmentPadding = 0
        text.setAccessibilityLabel("연결 로그")
        scroll.documentView = text
        return scroll
    }

    func updateNSView(_ scroll: NSScrollView, context: Context) {
        guard let text = scroll.documentView as? NSTextView else { return }
        let value = lines.joined(separator: "\n")
        guard text.string != value else { return }
        let atBottom = scroll.contentView.bounds.maxY >= text.bounds.maxY - 24
        text.string = value
        let style = NSMutableParagraphStyle()
        style.lineSpacing = 5
        text.textStorage?.addAttribute(.paragraphStyle, value: style,
                                      range: NSRange(location: 0, length: (value as NSString).length))
        if atBottom { text.scrollToEndOfDocument(nil) }
    }
}
