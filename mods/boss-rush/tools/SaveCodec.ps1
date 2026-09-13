# 丝之歌存档格式的编解码.
#
# 游戏读写存档的链路 (见反编译的 GameManager.GetBytesForSaveJson 与
# TeamCherry.SharedUtils.Encryption):
#
#   .dat 文件 = BinaryFormatter 序列化的一个 string
#               该 string = Base64( AES-256-ECB-PKCS7( UTF-8( JSON ) ) )
#
# BinaryFormatter 写单个字符串时的字节布局恰好是固定的:
#
#   00                  SerializationHeaderRecord
#   01 00 00 00         rootId = 1
#   ff ff ff ff         headerId = -1
#   01 00 00 00         majorVersion = 1
#   00 00 00 00         minorVersion = 0
#   06                  BinaryObjectString
#   01 00 00 00         objectId = 1
#   <7bit 变长长度>     UTF-8 字节数
#   <UTF-8 字节>
#   0b                  MessageEnd
#
# 本文件只做这一种布局的读写, 因此不需要 BinaryFormatter 本身.

$Script:SilksongSaveKey = [System.Text.Encoding]::UTF8.GetBytes('UKu52ePUBwetZ9wNX88o54dnfKRu0T1l')

function New-AesEcbTransform {
    param([switch]$Decrypt)

    $aes = [System.Security.Cryptography.Aes]::Create()
    $aes.Mode = [System.Security.Cryptography.CipherMode]::ECB
    $aes.Padding = [System.Security.Cryptography.PaddingMode]::PKCS7
    $aes.Key = $Script:SilksongSaveKey
    if ($Decrypt) {
        return $aes.CreateDecryptor()
    }

    return $aes.CreateEncryptor()
}

function Write-SevenBitEncodedInt {
    param([int]$Value)

    $bytes = New-Object System.Collections.Generic.List[byte]
    $current = $Value
    while ($true) {
        $piece = $current -band 0x7F
        $current = $current -shr 7
        if ($current -ne 0) {
            $bytes.Add([byte]($piece -bor 0x80))
        }
        else {
            $bytes.Add([byte]$piece)
            break
        }
    }

    # 逗号保证返回 byte[] 本身, 不被 PowerShell 展开成逐个字节.
    return , $bytes.ToArray()
}

function Read-SevenBitEncodedInt {
    param([byte[]]$Bytes, [int]$Offset)

    $result = 0
    $shift = 0
    $index = $Offset
    while ($true) {
        $b = $Bytes[$index]
        $index++
        $result = $result -bor (($b -band 0x7F) -shl $shift)
        if (($b -band 0x80) -eq 0) {
            break
        }

        $shift += 7
    }

    return @{ Value = $result; Next = $index }
}

# 把 JSON 文本编码成 .dat 的字节内容.
function ConvertTo-GameSaveBytes {
    param([Parameter(Mandatory)][string]$Json)

    $plain = [System.Text.Encoding]::UTF8.GetBytes($Json)
    $encryptor = New-AesEcbTransform
    try {
        $cipher = $encryptor.TransformFinalBlock($plain, 0, $plain.Length)
    }
    finally {
        $encryptor.Dispose()
    }

    $payload = [System.Text.Encoding]::UTF8.GetBytes([System.Convert]::ToBase64String($cipher))
    $lengthPrefix = Write-SevenBitEncodedInt -Value $payload.Length

    # 逐段拼装, 不走 BinaryWriter: 它的重载解析会把 byte[] 参数吃掉.
    $stream = New-Object System.IO.MemoryStream
    try {
        $stream.WriteByte(0x00)
        $stream.Write([System.BitConverter]::GetBytes([int]1), 0, 4)
        $stream.Write([System.BitConverter]::GetBytes([int]-1), 0, 4)
        $stream.Write([System.BitConverter]::GetBytes([int]1), 0, 4)
        $stream.Write([System.BitConverter]::GetBytes([int]0), 0, 4)
        $stream.WriteByte(0x06)
        $stream.Write([System.BitConverter]::GetBytes([int]1), 0, 4)
        $stream.Write($lengthPrefix, 0, $lengthPrefix.Length)
        $stream.Write($payload, 0, $payload.Length)
        $stream.WriteByte(0x0B)
        return , $stream.ToArray()
    }
    finally {
        $stream.Dispose()
    }
}

# 把 .dat 的字节内容解回 JSON 文本.
function ConvertFrom-GameSaveBytes {
    param([Parameter(Mandatory)][byte[]]$Bytes)

    if ($Bytes.Length -lt 24 -or $Bytes[0] -ne 0x00) {
        throw '这不是丝之歌的存档: 头部落款不对.'
    }

    $offset = 17
    if ($Bytes[$offset] -ne 0x06) {
        throw ('这不是丝之歌的存档: 期望字符串记录, 实际是 0x{0:x2}.' -f $Bytes[$offset])
    }

    $offset += 5
    $length = Read-SevenBitEncodedInt -Bytes $Bytes -Offset $offset
    $offset = $length.Next
    $base64 = [System.Text.Encoding]::UTF8.GetString($Bytes, $offset, $length.Value)

    $cipher = [System.Convert]::FromBase64String($base64)
    $decryptor = New-AesEcbTransform -Decrypt
    try {
        $plain = $decryptor.TransformFinalBlock($cipher, 0, $cipher.Length)
    }
    finally {
        $decryptor.Dispose()
    }

    return [System.Text.Encoding]::UTF8.GetString($plain)
}

# 取字节内容的指纹, 用来做快速相等比较 (PowerShell 里逐字节循环太慢).
function Get-ByteFingerprint {
    param([Parameter(Mandatory)][byte[]]$Bytes)

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return [System.Convert]::ToBase64String($sha.ComputeHash($Bytes))
    }
    finally {
        $sha.Dispose()
    }
}

# 把 JSON 文本压成仓库里存的 .json.gz 字节.
function ConvertTo-GameSaveGzip {
    param([Parameter(Mandatory)][string]$Json)

    $plain = [System.Text.Encoding]::UTF8.GetBytes($Json)
    $stream = New-Object System.IO.MemoryStream
    $gzip = New-Object System.IO.Compression.GZipStream($stream, [System.IO.Compression.CompressionLevel]::Optimal, $true)
    try {
        $gzip.Write($plain, 0, $plain.Length)
    }
    finally {
        $gzip.Dispose()
    }

    return $stream.ToArray()
}

# 把仓库里的 .json.gz 字节解成 JSON 文本.
function ConvertFrom-GameSaveGzip {
    param([Parameter(Mandatory)][byte[]]$Bytes)

    $input = New-Object System.IO.MemoryStream(, $Bytes)
    $gzip = New-Object System.IO.Compression.GZipStream($input, [System.IO.Compression.CompressionMode]::Decompress)
    $output = New-Object System.IO.MemoryStream
    try {
        $gzip.CopyTo($output)
        return [System.Text.Encoding]::UTF8.GetString($output.ToArray())
    }
    finally {
        $gzip.Dispose()
        $output.Dispose()
        $input.Dispose()
    }
}
