package com.androidmanager.companion

import android.graphics.Bitmap
import android.graphics.Color
import com.google.zxing.BarcodeFormat
import com.google.zxing.EncodeHintType
import com.google.zxing.qrcode.QRCodeWriter
import org.json.JSONObject

object QRGenerator {
    fun generateQR(info: ConnectionInfo, pairCode: String, size: Int = 400): Bitmap {
        val content = JSONObject().apply {
            put("type", "AndroidManagerConnect")
            put("ip", info.ipAddress)
            put("port", info.adbPort)
            put("pairCode", pairCode)
            put("device", info.deviceName)
            info.wirelessConnectPort?.let { put("wirelessAdbPort", it) }
            info.wirelessPairingPort?.let { put("wirelessPairingPort", it) }
        }.toString()

        val hints = mapOf(
            EncodeHintType.CHARACTER_SET to "UTF-8",
            EncodeHintType.MARGIN to 2
        )
        val matrix = QRCodeWriter().encode(content, BarcodeFormat.QR_CODE, size, size, hints)
        val bitmap = Bitmap.createBitmap(size, size, Bitmap.Config.RGB_565)
        for (x in 0 until size) {
            for (y in 0 until size) {
                bitmap.setPixel(x, y, if (matrix[x, y]) Color.BLACK else Color.WHITE)
            }
        }
        return bitmap
    }
}
