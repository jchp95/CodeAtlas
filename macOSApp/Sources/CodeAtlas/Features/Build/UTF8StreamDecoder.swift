import Foundation

/// Decodes process output without losing UTF-8 characters split across pipe reads.
/// Invalid byte sequences are replaced by Swift's standard UTF-8 replacement behavior.
struct UTF8StreamDecoder {
    private var pending = Data()

    mutating func append(_ chunk: Data) -> String {
        pending.append(chunk)
        let bytes = Array(pending)
        let completeLength = Self.completePrefixLength(bytes)
        let complete = Data(bytes.prefix(completeLength))
        pending = Data(bytes.dropFirst(completeLength))
        return String(decoding: complete, as: UTF8.self)
    }

    mutating func finish() -> String {
        defer { pending.removeAll(keepingCapacity: false) }
        return String(decoding: pending, as: UTF8.self)
    }

    private static func completePrefixLength(_ bytes: [UInt8]) -> Int {
        guard !bytes.isEmpty else { return 0 }
        var index = bytes.count - 1
        var continuationCount = 0
        while index >= 0, bytes[index] & 0b1100_0000 == 0b1000_0000, continuationCount < 3 {
            continuationCount += 1
            index -= 1
        }
        guard index >= 0 else { return bytes.count }

        let lead = bytes[index]
        let expectedLength: Int
        if lead & 0b1000_0000 == 0 { expectedLength = 1 }
        else if lead & 0b1110_0000 == 0b1100_0000 { expectedLength = 2 }
        else if lead & 0b1111_0000 == 0b1110_0000 { expectedLength = 3 }
        else if lead & 0b1111_1000 == 0b1111_0000 { expectedLength = 4 }
        else { expectedLength = 1 }

        let availableLength = bytes.count - index
        return expectedLength > availableLength ? index : bytes.count
    }
}
