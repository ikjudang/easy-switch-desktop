import SwiftUI

@MainActor
struct ChannelNameEditor: View {
    @ObservedObject var model: AppModel
    let channel: Int
    let dismiss: () -> Void
    @State private var draft = ""
    @FocusState private var focused: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(model.t("채널 이름 변경", "Rename channel")).font(AppTheme.body(13, weight: .semibold))
            HStack(spacing: 8) {
                TextField("", text: $draft)
                    .textFieldStyle(.plain).font(AppTheme.body(13)).focused($focused)
                    .padding(.horizontal, 6).frame(height: 32)
                    .background(AppTheme.Inset, in: RoundedRectangle(cornerRadius: 8))
                    .overlay(RoundedRectangle(cornerRadius: 8).strokeBorder(AppTheme.BorderStrong, lineWidth: 1))
                    .onSubmit { save() }
                    .accessibilityLabel(model.t("채널 이름", "Channel name"))
                Button(action: save) {
                    Text(model.t("확인", "OK")).font(AppTheme.body(12)).frame(width: 48, height: 32)
                }
                .buttonStyle(AppActionStyle(outlined: true, radius: 8))
            }
        }
        .padding(12).frame(width: 268)
        .foregroundStyle(AppTheme.Text).background(AppTheme.Card)
        .preferredColorScheme(.dark)
        .onAppear { draft = model.channelLabel(channel); focused = true }
        .onExitCommand(perform: dismiss)
    }
    private func save() { model.renameChannel(channel, name: draft); dismiss() }
}
