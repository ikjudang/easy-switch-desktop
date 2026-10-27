import SwiftUI

@MainActor
struct ReceiverPairingView: View {
    @ObservedObject var model: AppModel
    var body: some View {
        ScrollView {
        VStack(alignment: .leading, spacing: 16) {
            Text(model.t("수신기에 새 기기 연결", "Connect a new receiver device")).font(AppTheme.body(22, weight: .heavy))
            Text(model.t("연결할 기기의 Easy-Switch 버튼을 길게 눌러 페어링 모드로 바꿔 줘. 앱은 기존 등록 기기를 삭제하지 않아.", "Hold the device's Easy-Switch button to enter pairing mode. Existing receiver registrations are preserved."))
                .font(AppTheme.body(13)).foregroundStyle(AppTheme.Text2)
            if model.receivers.isEmpty {
                Text(model.t("Bolt 또는 Unifying 수신기를 연결해 줘.", "Connect a Bolt or Unifying receiver.")).font(AppTheme.body(14))
            }
            ForEach(model.receivers) { receiver in
                HStack {
                    Text(receiver.name).font(AppTheme.body(14, weight: .bold))
                    Spacer()
                    Button(model.t("검색", "Search")) { model.beginPairing(receiver.id) }
                        .buttonStyle(AppActionStyle(outlined: true))
                        .disabled(model.pairingProgress.map { ["searching", "candidate", "authenticating"].contains($0.stage) } ?? false)
                }
            }
            if let progress = model.pairingProgress {
                if !progress.deviceName.isEmpty { Text(progress.deviceName).font(AppTheme.body(16, weight: .bold)) }
                Text(label(progress.stage)).font(AppTheme.body(13))
                if !progress.instruction.isEmpty { Text(model.t(progress.instruction, progress.instructionEnglish)).font(AppTheme.mono(14)) }
                HStack {
                    if progress.stage == "candidate" {
                        Button(model.t("이 기기 연결", "Connect this device")) { model.confirmPairing(progress.receiverID) }
                            .buttonStyle(AppActionStyle(prominent: true))
                    }
                    if ["searching", "candidate", "authenticating"].contains(progress.stage) {
                        Button(model.t("연결 취소", "Cancel pairing")) { model.cancelPairing(progress.receiverID) }
                            .buttonStyle(AppActionStyle(outlined: true))
                    }
                }
            }
            Spacer(minLength: 0)
        }
        .padding(24).frame(maxWidth: .infinity, alignment: .leading)
        }
        .frame(width: 460, height: 460)
        .foregroundStyle(AppTheme.Text).background(AppTheme.Bg)
    }
    private func label(_ stage: String) -> String {
        switch stage {
        case "searching": model.t("앱이 페어링 중인 기기를 찾고 있어.", "EasySwitch is searching for a device in pairing mode.")
        case "candidate": model.t("기기 이름을 확인한 뒤 연결을 눌러 줘.", "Check the device name, then choose Connect.")
        case "authenticating": model.t("아래 안내에 따라 기기에서 연결을 확인해 줘.", "Confirm the connection on the device as shown below.")
        case "complete": model.t("수신기에 기기를 등록했어.", "The device is registered with the receiver.")
        default: model.t("연결 절차가 끝났어. 다시 연결하려면 검색을 눌러 줘.", "Pairing has ended. Choose Search to try again.")
        }
    }
}
