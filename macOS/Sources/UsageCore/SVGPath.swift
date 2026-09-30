import Foundation
import CoreGraphics

/// Converts SVG path data (M L H V C S Q T A Z, absolute and relative) to a CGPath in SVG coordinates.
/// Avoids NSImage SVG loading, which macOS 13 doesn't provide. Malformed input stops at the last valid command.
public enum SVGPath {
    public static func cgPath(_ data: String) -> CGPath {
        var scanner = Scanner(Array(data.utf8))
        let path = CGMutablePath()
        var current = CGPoint.zero, start = CGPoint.zero, lastControl: CGPoint?
        var command: UInt8 = 0, previous: UInt8 = 0
        while true {
            if let next = scanner.command() { command = next }
            else if command == 0 || !scanner.hasNumber() { break }
            else if command == UInt8(ascii: "M") { command = UInt8(ascii: "L") }
            else if command == UInt8(ascii: "m") { command = UInt8(ascii: "l") }
            let relative = command >= UInt8(ascii: "a")
            let upper = relative ? command - 32 : command
            func point() -> CGPoint? {
                guard let x = scanner.number(), let y = scanner.number() else { return nil }
                return relative ? CGPoint(x: current.x + x, y: current.y + y) : CGPoint(x: x, y: y)
            }
            // Smooth curves reflect only a control point from the same curve family.
            let smooth = upper == UInt8(ascii: "S") ? [UInt8(ascii: "C"), UInt8(ascii: "S")].contains(previous)
                : [UInt8(ascii: "Q"), UInt8(ascii: "T")].contains(previous)
            switch upper {
            case UInt8(ascii: "Z"):
                path.closeSubpath(); current = start; lastControl = nil
                previous = upper; command = 0; continue
            case UInt8(ascii: "M"):
                guard let p = point() else { return path }
                path.move(to: p); current = p; start = p; lastControl = nil
            case UInt8(ascii: "L"):
                guard let p = point() else { return path }
                path.addLine(to: p); current = p; lastControl = nil
            case UInt8(ascii: "H"):
                guard let x = scanner.number() else { return path }
                current.x = relative ? current.x + x : x
                path.addLine(to: current); lastControl = nil
            case UInt8(ascii: "V"):
                guard let y = scanner.number() else { return path }
                current.y = relative ? current.y + y : y
                path.addLine(to: current); lastControl = nil
            case UInt8(ascii: "C"), UInt8(ascii: "S"):
                let reflected = smooth ? lastControl.map { CGPoint(x: 2 * current.x - $0.x, y: 2 * current.y - $0.y) } : nil
                guard let c1 = upper == UInt8(ascii: "C") ? point() : (reflected ?? current), let c2 = point(), let p = point() else { return path }
                path.addCurve(to: p, control1: c1, control2: c2); current = p; lastControl = c2
            case UInt8(ascii: "Q"), UInt8(ascii: "T"):
                let reflected = smooth ? lastControl.map { CGPoint(x: 2 * current.x - $0.x, y: 2 * current.y - $0.y) } : nil
                guard let c = upper == UInt8(ascii: "Q") ? point() : (reflected ?? current), let p = point() else { return path }
                path.addQuadCurve(to: p, control: c); current = p; lastControl = c
            case UInt8(ascii: "A"):
                guard let rx = scanner.number(), let ry = scanner.number(), let rotation = scanner.number(),
                      let large = scanner.flag(), let sweep = scanner.flag(), let p = point() else { return path }
                arc(path, from: current, to: p, rx: rx, ry: ry, degrees: rotation, large: large, sweep: sweep)
                current = p; lastControl = nil
            default: return path
            }
            previous = upper
        }
        return path
    }

    /// SVG 1.1 implementation notes F.6.5–F.6.6: endpoint to center parameterization, then ≤90° cubic segments.
    private static func arc(_ path: CGMutablePath, from p1: CGPoint, to p2: CGPoint, rx: CGFloat, ry: CGFloat,
                            degrees: CGFloat, large: Bool, sweep: Bool) {
        var rx = abs(rx), ry = abs(ry)
        guard rx > 0, ry > 0, p1 != p2 else { if p1 != p2 { path.addLine(to: p2) }; return }
        let phi = degrees * .pi / 180, cosPhi = cos(phi), sinPhi = sin(phi)
        let dx = (p1.x - p2.x) / 2, dy = (p1.y - p2.y) / 2
        let x1 = cosPhi * dx + sinPhi * dy, y1 = -sinPhi * dx + cosPhi * dy
        let lambda = (x1 * x1) / (rx * rx) + (y1 * y1) / (ry * ry)
        if lambda > 1 { rx *= sqrt(lambda); ry *= sqrt(lambda) }
        let numerator = rx * rx * ry * ry - rx * rx * y1 * y1 - ry * ry * x1 * x1
        let denominator = rx * rx * y1 * y1 + ry * ry * x1 * x1
        let coefficient = (large == sweep ? -1 : 1) * sqrt(max(0, numerator / denominator))
        let cx1 = coefficient * rx * y1 / ry, cy1 = -coefficient * ry * x1 / rx
        let cx = cosPhi * cx1 - sinPhi * cy1 + (p1.x + p2.x) / 2
        let cy = sinPhi * cx1 + cosPhi * cy1 + (p1.y + p2.y) / 2
        func angle(_ ux: CGFloat, _ uy: CGFloat, _ vx: CGFloat, _ vy: CGFloat) -> CGFloat { atan2(ux * vy - uy * vx, ux * vx + uy * vy) }
        let theta = angle(1, 0, (x1 - cx1) / rx, (y1 - cy1) / ry)
        var delta = angle((x1 - cx1) / rx, (y1 - cy1) / ry, (-x1 - cx1) / rx, (-y1 - cy1) / ry)
        if !sweep && delta > 0 { delta -= 2 * .pi } else if sweep && delta < 0 { delta += 2 * .pi }
        func map(_ ux: CGFloat, _ uy: CGFloat) -> CGPoint {
            CGPoint(x: cx + rx * ux * cosPhi - ry * uy * sinPhi, y: cy + rx * ux * sinPhi + ry * uy * cosPhi)
        }
        let segments = max(1, Int(ceil(abs(delta) / (.pi / 2) - 0.001)))
        let step = delta / CGFloat(segments), t = 4 / 3 * tan(step / 4)
        var a0 = theta
        for index in 0..<segments {
            let a1 = a0 + step
            let end = index == segments - 1 ? p2 : map(cos(a1), sin(a1))
            path.addCurve(to: end, control1: map(cos(a0) - t * sin(a0), sin(a0) + t * cos(a0)),
                          control2: map(cos(a1) + t * sin(a1), sin(a1) - t * cos(a1)))
            a0 = a1
        }
    }

    private struct Scanner {
        let bytes: [UInt8]
        var index = 0
        init(_ bytes: [UInt8]) { self.bytes = bytes }
        private mutating func skipSeparators() {
            while index < bytes.count, [32, 9, 10, 13, 44].contains(bytes[index]) { index += 1 }
        }
        mutating func command() -> UInt8? {
            skipSeparators()
            guard index < bytes.count else { return nil }
            let byte = bytes[index] | 32
            guard byte >= UInt8(ascii: "a"), byte <= UInt8(ascii: "z"), byte != UInt8(ascii: "e") else { return nil }
            index += 1
            return bytes[index - 1]
        }
        mutating func hasNumber() -> Bool {
            skipSeparators()
            guard index < bytes.count else { return false }
            let byte = bytes[index]
            return (byte >= 48 && byte <= 57) || byte == 45 || byte == 43 || byte == 46
        }
        /// Arc flags are single digits and may be written without separators, e.g. `a1 1 0 01.5.5`.
        mutating func flag() -> Bool? {
            skipSeparators()
            guard index < bytes.count, bytes[index] == 48 || bytes[index] == 49 else { return nil }
            index += 1
            return bytes[index - 1] == 49
        }
        mutating func number() -> CGFloat? {
            guard hasNumber() else { return nil }
            let begin = index
            if bytes[index] == 45 || bytes[index] == 43 { index += 1 }
            var dot = false, digits = false
            while index < bytes.count {
                let byte = bytes[index]
                if byte >= 48 && byte <= 57 { digits = true; index += 1 }
                else if byte == 46 && !dot { dot = true; index += 1 }
                else { break }
            }
            if digits, index < bytes.count, bytes[index] | 32 == UInt8(ascii: "e") {
                var look = index + 1
                if look < bytes.count, bytes[look] == 45 || bytes[look] == 43 { look += 1 }
                if look < bytes.count, bytes[look] >= 48 && bytes[look] <= 57 {
                    index = look
                    while index < bytes.count, bytes[index] >= 48 && bytes[index] <= 57 { index += 1 }
                }
            }
            guard digits, let value = Double(String(decoding: bytes[begin..<index], as: UTF8.self)) else { index = begin; return nil }
            return CGFloat(value)
        }
    }
}
