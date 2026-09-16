package com.androidmanager.companion

import android.content.Context
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import android.net.wifi.WifiManager
import android.os.Build
import java.net.Inet4Address

class NetworkMonitor(context: Context) {

    private val appContext = context.applicationContext
    private var generation = 0
    private var lastIp: String? = null
    private var lastSsidHash: Int? = null

    fun currentState(): NetworkState {
        val cm = appContext.getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager
        val network = cm.activeNetwork
        val props = cm.getLinkProperties(network)

        val currentIp = props?.linkAddresses
            ?.firstOrNull { it.address is Inet4Address && !it.address.isLoopbackAddress }
            ?.address?.hostAddress

        val ssidHash = readSsidHash()

        if (currentIp != lastIp || ssidHash != lastSsidHash) {
            generation++
            lastIp = currentIp
            lastSsidHash = ssidHash
        }

        return NetworkState(
            generation = generation,
            ipAddress = currentIp,
            networkType = detectNetworkType(cm, network),
            ssidHash = ssidHash
        )
    }

    private fun readSsidHash(): Int? {
        return try {
            @Suppress("DEPRECATION")
            val wm = appContext.getSystemService(Context.WIFI_SERVICE) as WifiManager
            @Suppress("DEPRECATION")
            wm.connectionInfo?.ssid?.hashCode()
        } catch (_: Exception) {
            null
        }
    }

    private fun detectNetworkType(cm: ConnectivityManager, network: android.net.Network?): String {
        val caps = cm.getNetworkCapabilities(network) ?: return "unknown"
        return when {
            caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) -> "wifi"
            caps.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR) -> "cellular"
            caps.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET) -> "ethernet"
            else -> "other"
        }
    }
}

data class NetworkState(
    val generation: Int,
    val ipAddress: String?,
    val networkType: String,
    val ssidHash: Int?
)
