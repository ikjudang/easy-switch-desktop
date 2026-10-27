import AppKit

@MainActor
enum StatusBarIcon {
    static func makeImage(size: CGFloat = 18) -> NSImage {
        // Match the launcher's original 332:152 foreground at 22 units wide
        // inside the 24 × 24 canvas, including its 16-unit strokes and filled arrows.
        let image = NSImage(size: NSSize(width: size, height: size), flipped: true) { bounds in
            guard let context = NSGraphicsContext.current?.cgContext else { return false }
            context.saveGState()
            defer { context.restoreGState() }
            context.translateBy(x: bounds.minX, y: bounds.minY)
            context.scaleBy(x: bounds.width / 24, y: bounds.height / 24)
            context.translateBy(x: -5.891566265060, y: -4.963855421687)
            context.scaleBy(x: 0.066265060241, y: 0.066265060241)
            context.setStrokeColor(CGColor(gray: 0, alpha: 1))
            context.setFillColor(CGColor(gray: 0, alpha: 1))
            context.setLineCap(.round)
            context.setLineJoin(.round)
            context.setLineWidth(16)

            let devices = CGMutablePath()
            devices.addRoundedRect(in: CGRect(x: 112, y: 188, width: 70, height: 136),
                                   cornerWidth: 13, cornerHeight: 13)
            devices.move(to: CGPoint(x: 139, y: 311))
            devices.addLine(to: CGPoint(x: 155, y: 311))
            devices.addRoundedRect(in: CGRect(x: 330, y: 200, width: 98, height: 74),
                                   cornerWidth: 11, cornerHeight: 11)
            devices.move(to: CGPoint(x: 379, y: 274))
            devices.addLine(to: CGPoint(x: 379, y: 296))
            devices.move(to: CGPoint(x: 356, y: 299))
            devices.addLine(to: CGPoint(x: 402, y: 299))
            context.addPath(devices)
            context.strokePath()

            context.addPath(CGPath(roundedRect: CGRect(x: 212, y: 228, width: 64, height: 15),
                                   cornerWidth: 7, cornerHeight: 7, transform: nil))
            context.fillPath()
            let rightArrow = CGMutablePath()
            rightArrow.move(to: CGPoint(x: 274, y: 216))
            rightArrow.addLine(to: CGPoint(x: 300, y: 235.5))
            rightArrow.addLine(to: CGPoint(x: 274, y: 255))
            rightArrow.closeSubpath()
            context.addPath(rightArrow)
            context.fillPath()
            context.addPath(CGPath(roundedRect: CGRect(x: 236, y: 270, width: 64, height: 15),
                                   cornerWidth: 7, cornerHeight: 7, transform: nil))
            context.fillPath()
            let leftArrow = CGMutablePath()
            leftArrow.move(to: CGPoint(x: 238, y: 262))
            leftArrow.addLine(to: CGPoint(x: 212, y: 281.5))
            leftArrow.addLine(to: CGPoint(x: 238, y: 301))
            leftArrow.closeSubpath()
            context.addPath(leftArrow)
            context.fillPath()
            return true
        }
        image.isTemplate = true
        image.accessibilityDescription = "EasySwitch"
        return image
    }
}
