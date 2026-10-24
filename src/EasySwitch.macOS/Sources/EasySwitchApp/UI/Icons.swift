import AppKit
import SwiftUI

/// App-owned 24-point vector artwork; no Windows icon-font dependency.
struct AppGlyph: View {
    let kind: String
    var size: CGFloat = 20
    var tint: Color = AppTheme.Text2

    var body: some View {
        Canvas { context, canvasSize in
            context.scaleBy(x: canvasSize.width / 24, y: canvasSize.height / 24)
            var path = Path()
            func line(_ points: [CGPoint], close: Bool = false) {
                guard let first = points.first else { return }
                path.move(to: first)
                for point in points.dropFirst() { path.addLine(to: point) }
                if close { path.closeSubpath() }
            }
            var fill = false
            switch kind {
            case "home":
                line([.init(x: 12, y: 3), .init(x: 21, y: 11), .init(x: 18.5, y: 11),
                      .init(x: 18.5, y: 20), .init(x: 5.5, y: 20), .init(x: 5.5, y: 11),
                      .init(x: 3, y: 11)], close: true)
                fill = true
            case "devices":
                for y in [7.0, 12.0, 17.0] { line([.init(x: 4, y: y), .init(x: 20, y: y)]) }
            case "keyboard":
                path.addRoundedRect(in: CGRect(x: 2.5, y: 5, width: 19, height: 15), cornerSize: .init(width: 3, height: 3))
                for x in [6.5, 10.8, 15.1, 18.5] {
                    line([.init(x: x, y: 9), .init(x: x + 0.05, y: 9)])
                }
                line([.init(x: 8, y: 15), .init(x: 17, y: 15)])
            case "mouse":
                path.addRoundedRect(in: CGRect(x: 7, y: 2, width: 10, height: 20), cornerSize: .init(width: 5, height: 5))
                line([.init(x: 12, y: 5), .init(x: 12, y: 9)])
            case "refresh":
                path.addArc(center: .init(x: 12, y: 12), radius: 7.5,
                            startAngle: .degrees(-30), endAngle: .degrees(320), clockwise: false)
                line([.init(x: 20, y: 6), .init(x: 20, y: 11), .init(x: 15, y: 11)])
            case "clock":
                path.addEllipse(in: .init(x: 4, y: 4, width: 16, height: 16))
                line([.init(x: 12, y: 7.5), .init(x: 12, y: 12), .init(x: 15.2, y: 13.8)])
            case "folder":
                line([.init(x: 3, y: 7), .init(x: 9.5, y: 7), .init(x: 11.5, y: 9.5),
                      .init(x: 21, y: 9.5), .init(x: 21, y: 19), .init(x: 3, y: 19)], close: true)
                line([.init(x: 3, y: 7), .init(x: 3, y: 5), .init(x: 10, y: 5),
                      .init(x: 12, y: 7), .init(x: 21, y: 7), .init(x: 21, y: 9.5)])
            case "follow":
                line([.init(x: 3, y: 7), .init(x: 15, y: 7)])
                line([.init(x: 11, y: 3), .init(x: 15, y: 7), .init(x: 11, y: 11)])
                line([.init(x: 21, y: 17), .init(x: 9, y: 17)])
                line([.init(x: 13, y: 13), .init(x: 9, y: 17), .init(x: 13, y: 21)])
            case "check":
                line([.init(x: 6, y: 12), .init(x: 10, y: 16), .init(x: 18, y: 8)])
            case "chevron-right":
                line([.init(x: 9, y: 6), .init(x: 15, y: 12), .init(x: 9, y: 18)])
            case "chevron-left":
                line([.init(x: 15, y: 6), .init(x: 9, y: 12), .init(x: 15, y: 18)])
            case "chevron-down":
                line([.init(x: 6, y: 9), .init(x: 12, y: 15), .init(x: 18, y: 9)])
            case "globe":
                path.addEllipse(in: .init(x: 3, y: 3, width: 18, height: 18))
                path.addEllipse(in: .init(x: 8, y: 3, width: 8, height: 18))
                line([.init(x: 3, y: 12), .init(x: 21, y: 12)])
            case "gear":
                // The same eight-spoke silhouette as the PC artwork.
                let radii: [CGFloat] = [9.6, 9.6, 7.4, 7.4]
                let center = CGPoint(x: 12, y: 12)
                for index in 0..<32 {
                    let angle = Double(index) * .pi / 16 - .pi / 2
                    let radius = radii[index % 4]
                    let point = CGPoint(x: center.x + radius * cos(angle), y: center.y + radius * sin(angle))
                    if index == 0 { path.move(to: point) } else { path.addLine(to: point) }
                }
                path.closeSubpath()
                path.addEllipse(in: .init(x: 8.4, y: 8.4, width: 7.2, height: 7.2))
                context.fill(path, with: .color(tint), style: .init(eoFill: true))
                return
            default:
                path.addRoundedRect(in: .init(x: 4, y: 4, width: 16, height: 16), cornerSize: .init(width: 3, height: 3))
            }
            if fill { context.fill(path, with: .color(tint)) }
            else { context.stroke(path, with: .color(tint), style: .init(lineWidth: 2, lineCap: .round, lineJoin: .round)) }
        }
        .frame(width: size, height: size)
        .accessibilityHidden(true)
    }
}

struct AppBrandMark: View {
    var size: CGFloat = 34
    var active = true

    var body: some View {
        RoundedRectangle(cornerRadius: size * 0.294).fill(active ? AppTheme.Point : AppTheme.Inset)
            .overlay {
                Canvas { context, bounds in
                    // Match the enlarged foreground in AppIcon and EasySwitchMark.svg;
                    // keep the header's existing rounded tile and 34-point layout.
                    context.translateBy(x: bounds.width / 2, y: bounds.height / 2)
                    context.scaleBy(x: bounds.width * 1.16 / 512, y: bounds.height * 1.16 / 512)
                    context.translateBy(x: -270, y: -256)
                    var outline = Path()
                    outline.addRoundedRect(in: .init(x: 112, y: 188, width: 70, height: 136), cornerSize: .init(width: 13, height: 13))
                    outline.addRoundedRect(in: .init(x: 330, y: 200, width: 98, height: 74), cornerSize: .init(width: 11, height: 11))
                    outline.move(to: .init(x: 139, y: 311)); outline.addLine(to: .init(x: 155, y: 311))
                    outline.move(to: .init(x: 379, y: 274)); outline.addLine(to: .init(x: 379, y: 296))
                    outline.move(to: .init(x: 356, y: 299)); outline.addLine(to: .init(x: 402, y: 299))
                    context.stroke(outline, with: .color(active ? .white : AppTheme.Text2), style: .init(lineWidth: 16, lineCap: .round, lineJoin: .round))
                    var arrows = Path()
                    arrows.addRoundedRect(in: .init(x: 212, y: 228, width: 64, height: 15), cornerSize: .init(width: 7, height: 7))
                    arrows.move(to: .init(x: 274, y: 216)); arrows.addLine(to: .init(x: 300, y: 235.5))
                    arrows.addLine(to: .init(x: 274, y: 255)); arrows.closeSubpath()
                    arrows.addRoundedRect(in: .init(x: 236, y: 270, width: 64, height: 15), cornerSize: .init(width: 7, height: 7))
                    arrows.move(to: .init(x: 238, y: 301)); arrows.addLine(to: .init(x: 212, y: 281.5))
                    arrows.addLine(to: .init(x: 238, y: 262)); arrows.closeSubpath()
                    context.fill(arrows, with: .color(active ? .white : AppTheme.Text2))
                }
            }
            .frame(width: size, height: size)
            .accessibilityHidden(true)
    }
}

/// Settings uses the floating home's existing shallow bevel, including its shadow.
@MainActor
struct FloatingSettingsPreview: View {
    var size: CGFloat = 40
    var active = true

    var body: some View {
        Image(decorative: active ? FloatingArtwork.image(-1) : Self.inactiveArtwork, scale: 6)
            .resizable()
            .interpolation(.high)
            .frame(width: Self.paintSize(for: size), height: Self.paintSize(for: size))
            .frame(width: size, height: size)
            .accessibilityHidden(true)
    }

    // Android's settings preview and Windows BubbleIcon both scale their 54-unit
    // home face by layoutSize / 60, giving a 36-point face in this 40-point slot.
    static func paintSize(for size: CGFloat) -> CGFloat { FloatingArtwork.canvas * size / 60 }

    // Port of Android inactiveHomeIconPaint / Windows InactiveIconEffect.
    // Inputs and output are normalized premultiplied RGBA, like the Windows shader.
    static func inactivePixel(_ pixel: SIMD4<Double>) -> SIMD4<Double> {
        let alpha = pixel.w
        let luma = (0.299 * pixel.x + 0.587 * pixel.y + 0.114 * pixel.z) / max(alpha, 0.00001) * 255
        let baseLuma = 0.299 * 108 + 0.587 * 60 + 0.114 * 224
        let position = (luma - baseLuma) / (255 - baseLuma)
        func channel(_ low: Double, _ high: Double) -> Double {
            min(1, max(0, (low + (high - low) * position) / 255)) * alpha
        }
        return SIMD4(channel(32, 139), channel(38, 147), channel(45, 157), alpha)
    }

    private static let inactiveArtwork: CGImage = {
        let source = FloatingArtwork.image(-1)
        let rowBytes = source.width * 4
        let context = CGContext(data: nil, width: source.width, height: source.height,
                                bitsPerComponent: 8, bytesPerRow: rowBytes, space: CGColorSpaceCreateDeviceRGB(),
                                bitmapInfo: CGBitmapInfo.byteOrder32Big.rawValue | CGImageAlphaInfo.premultipliedLast.rawValue)!
        context.draw(source, in: CGRect(x: 0, y: 0, width: source.width, height: source.height))
        let bytes = context.data!.assumingMemoryBound(to: UInt8.self)
        for index in stride(from: 0, to: rowBytes * source.height, by: 4) {
            let pixel = SIMD4(Double(bytes[index]) / 255, Double(bytes[index + 1]) / 255,
                              Double(bytes[index + 2]) / 255, Double(bytes[index + 3]) / 255)
            let mapped = inactivePixel(pixel)
            for channel in 0..<3 { bytes[index + channel] = UInt8(clamping: Int((mapped[channel] * 255).rounded())) }
            // Keep the original alpha byte; filtering must not change the silhouette.
        }
        return context.makeImage()!
    }()
}
