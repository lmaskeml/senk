package com.androidmanager.companion

import android.content.Context
import android.net.wifi.WifiManager
import android.os.Build
import android.provider.Settings
import java.net.NetworkInterface

class AdbWifiManager(private val context: Context) {

    companion object {
        const val DEFAULT_ADB_PORT = 5555
    }

    fun getDeviceId(): String =
        try {
            Settings.Secure.getString(context.contentResolver, Settings.Secure.ANDROID_ID) ?: ""
        } catch (_: Exception) {
            ""
        }

    fun getLocalIpAddress(): String? {
        try {
            @Suppress("DEPRECATION")
            val wifiManager = context.applicationContext
                .getSystemService(Context.WIFI_SERVICE) as WifiManager
            @Suppress("DEPRECATION")
            val ipInt = wifiManager.connectionInfo.ipAddress
            if (ipInt != 0) {
                return String.format(
                    "%d.%d.%d.%d",
                    ipInt and 0xff,
                    ipInt shr 8 and 0xff,
                    ipInt shr 16 and 0xff,
                    ipInt shr 24 and 0xff
                )
            }
        } catch (_: Exception) {
        }

        return try {
            NetworkInterface.getNetworkInterfaces()?.toList()?.flatMap { it.inetAddresses.toList() }
                ?.firstOrNull { !it.isLoopbackAddress && it is java.net.Inet4Address }
                ?.hostAddress
        } catch (_: Exception) {
            null
        }
    }

    fun enableAdbWifi(): AdbEnableResult {
        return try {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
                AdbEnableResult.NeedSettings
            } else {
                val process = Runtime.getRuntime().exec(
                    arrayOf(
                        "sh", "-c",
                        "setprop service.adb.tcp.port $DEFAULT_ADB_PORT && stop adbd && start adbd"
                    )
                )
                process.waitFor()
                AdbEnableResult.Success(getLocalIpAddress() ?: "?", DEFAULT_ADB_PORT)
            }
        } catch (e: Exception) {
            AdbEnableResult.Error(e.message ?: "Bilinmeyen hata")
        }
    }

    fun getConnectionInfo(): ConnectionInfo {
        val ip = getLocalIpAddress() ?: "Bağlı değil"
        val wireless = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            WirelessDebuggingHelper.refreshFromProperties()
            WirelessDebuggingHelper.currentPorts()
        } else {
            WirelessDebugPorts()
        }

        val effectiveAdbPort = wireless.connectPort ?: DEFAULT_ADB_PORT

        return ConnectionInfo(
            ipAddress = ip,
            adbPort = effectiveAdbPort,
            legacyAdbPort = DEFAULT_ADB_PORT,
            wirelessConnectPort = wireless.connectPort,
            wirelessPairingPort = wireless.pairingPort,
            wirelessDebugEnabled = wireless.wifiDebugLikelyEnabled,
            wirelessPortSource = wireless.source,
            deviceId = getDeviceId(),
            deviceModel = Build.MODEL,
            deviceName = "${Build.MANUFACTURER} ${Build.MODEL}",
            androidVersion = Build.VERSION.RELEASE,
            sdkVersion = Build.VERSION.SDK_INT
        )
    }
}

data class ConnectionInfo(
    val ipAddress: String,
    /** Best ADB connect port for PC (wireless TLS if known, else 5555). */
    val adbPort: Int,
    val legacyAdbPort: Int = 5555,
    val wirelessConnectPort: Int? = null,
    val wirelessPairingPort: Int? = null,
    val wirelessDebugEnabled: Boolean = false,
    val wirelessPortSource: String = "none",
    val deviceId: String = "",
    val deviceModel: String,
    val deviceName: String,
    val androidVersion: String,
    val sdkVersion: Int
)

sealed class AdbEnableResult {
    data class Success(val ip: String, val port: Int) : AdbEnableResult()
    data object NeedSettings : AdbEnableResult()
    data class Error(val message: String) : AdbEnableResult()
}
