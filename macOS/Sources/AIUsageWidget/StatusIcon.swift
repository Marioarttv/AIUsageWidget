import AppKit
import UsageCore

/// The menu-bar item as one template image, so macOS tints the marks and numbers together for any menu bar.
/// One mark and number per provider; several accounts of a provider are merged into one number beforehand.
enum StatusIcon {
    struct Reading { let provider: ProviderID; let value: String; let name: String }
    private static let height: CGFloat = 18, mark: CGFloat = 13, markGap: CGFloat = 2, readingGap: CGFloat = 7
    private static let attributes: [NSAttributedString.Key: Any] = [
        .font: NSFont.monospacedDigitSystemFont(ofSize: 11, weight: .medium), .foregroundColor: NSColor.black]

    static func image(_ readings: [Reading]) -> NSImage {
        let texts = readings.map { NSAttributedString(string: $0.value, attributes: attributes) }
        let width = texts.reduce(CGFloat(0)) { $0 + mark + markGap + ceil($1.size().width) }
            + readingGap * CGFloat(max(0, texts.count - 1))
        let image = NSImage(size: NSSize(width: width, height: height), flipped: true) { _ in
            guard let context = NSGraphicsContext.current?.cgContext else { return false }
            var x: CGFloat = 0
            for (index, (reading, text)) in zip(readings, texts).enumerated() {
                if index > 0 { x += readingGap }
                context.saveGState()
                context.translateBy(x: x, y: (height - mark) / 2)
                context.scaleBy(x: mark / BrandMark.canvas, y: mark / BrandMark.canvas)
                context.addPath(BrandMark.path(for: reading.provider))
                context.setFillColor(NSColor.black.cgColor)
                context.fillPath()
                context.restoreGState()
                x += mark + markGap
                let size = text.size()
                text.draw(at: NSPoint(x: x, y: (height - size.height) / 2))
                x += ceil(size.width)
            }
            return true
        }
        image.isTemplate = true
        image.accessibilityDescription = readings.map { "\($0.name) \($0.value)" }.joined(separator: ", ")
        return image
    }
}
