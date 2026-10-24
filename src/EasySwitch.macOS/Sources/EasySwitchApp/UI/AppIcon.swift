import AppKit

/// Unmasked source artwork for exports. The system app icon is compiled from
/// Assets/AppIcon.icon so macOS, rather than this exporter, applies the mask.
/// The rounded in-app brand mark is drawn separately by AppBrandMark.
@MainActor
enum AppIcon {
    static func makeImage(size: CGFloat = 128) -> NSImage {
        let image = NSImage(size: NSSize(width: size, height: size))
        if let bitmap = try? bitmap(size: size) { image.addRepresentation(bitmap) }
        return image
    }

    /// Writes an opaque, full-bleed 1024 × 1024 source image with the app icon artwork.
    static func writePNG(to url: URL) throws {
        guard let png = try bitmap(size: 1024).representation(using: .png, properties: [:]) else {
            throw IconError.encodingFailed
        }
        try png.write(to: url, options: .atomic)
    }

    private static func bitmap(size: CGFloat) throws -> NSBitmapImageRep {
        guard size.isFinite, size >= 1, size <= 4096 else { throw IconError.invalidSize }
        let side = Int(size.rounded(.up))
        guard let space = CGColorSpace(name: CGColorSpace.sRGB),
              let context = CGContext(data: nil, width: side, height: side, bitsPerComponent: 8,
                                      bytesPerRow: 0, space: space,
                                      bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else {
            throw IconError.bitmapAllocationFailed
        }
        context.setAllowsAntialiasing(true)
        context.setShouldAntialias(true)
        context.translateBy(x: 0, y: CGFloat(side))
        context.scaleBy(x: CGFloat(side) / size, y: -CGFloat(side) / size)
        draw(in: context, size: size)
        guard let image = context.makeImage() else { throw IconError.bitmapAllocationFailed }
        let bitmap = NSBitmapImageRep(cgImage: image)
        bitmap.size = NSSize(width: size, height: size)
        return bitmap
    }

    private static func draw(in context: CGContext, size: CGFloat) {
        context.setFillColor(CGColor(srgbRed: 108 / 255, green: 60 / 255, blue: 224 / 255, alpha: 1))
        context.fill(CGRect(x: 0, y: 0, width: size, height: size))

        // Match Assets/AppIcon.icon/Assets/EasySwitchMark.svg. The Android launcher
        // geometry fills about 75% of the icon width, with balanced internal gaps.
        context.translateBy(x: size / 2, y: size / 2)
        context.scaleBy(x: size * 1.16 / 512, y: size * 1.16 / 512)
        context.translateBy(x: -270, y: -256)
        context.setStrokeColor(CGColor(gray: 1, alpha: 1))
        context.setFillColor(CGColor(gray: 1, alpha: 1))
        context.setLineWidth(16)
        context.setLineCap(.round)
        context.setLineJoin(.round)
        let outline = CGMutablePath()
        outline.addRoundedRect(in: CGRect(x: 112, y: 188, width: 70, height: 136), cornerWidth: 13, cornerHeight: 13)
        outline.addRoundedRect(in: CGRect(x: 330, y: 200, width: 98, height: 74), cornerWidth: 11, cornerHeight: 11)
        outline.move(to: CGPoint(x: 139, y: 311)); outline.addLine(to: CGPoint(x: 155, y: 311))
        outline.move(to: CGPoint(x: 379, y: 274)); outline.addLine(to: CGPoint(x: 379, y: 296))
        outline.move(to: CGPoint(x: 356, y: 299)); outline.addLine(to: CGPoint(x: 402, y: 299))
        context.addPath(outline)
        context.strokePath()
        let arrows = CGMutablePath()
        arrows.addRoundedRect(in: CGRect(x: 212, y: 228, width: 64, height: 15), cornerWidth: 7, cornerHeight: 7)
        arrows.move(to: CGPoint(x: 274, y: 216)); arrows.addLine(to: CGPoint(x: 300, y: 235.5))
        arrows.addLine(to: CGPoint(x: 274, y: 255)); arrows.closeSubpath()
        arrows.addRoundedRect(in: CGRect(x: 236, y: 270, width: 64, height: 15), cornerWidth: 7, cornerHeight: 7)
        // Match the shaft's winding so the overlap stays filled, as in the SVG.
        arrows.move(to: CGPoint(x: 238, y: 301)); arrows.addLine(to: CGPoint(x: 212, y: 281.5))
        arrows.addLine(to: CGPoint(x: 238, y: 262)); arrows.closeSubpath()
        context.addPath(arrows)
        context.fillPath()
    }

    private enum IconError: Error { case invalidSize, bitmapAllocationFailed, encodingFailed }
}
