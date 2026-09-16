package com.androidmanager.companion

import android.content.Context
import android.content.Intent
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import android.os.Build
import android.provider.Settings
import java.util.concurrent.atomic.AtomicReference

/**
 * Reads Android 11+ wireless-debugging ports when the OS exposes them.
 *
 * - Connect port: often in [service.adb.tls.port] (may be blocked for 3rd-party apps).
 * - Also discovers local mDNS `_adb-tls-connect` / `_adb-tls-pairing` via [NsdManager].
 * - Pairing port only exists while “Eşleştirme koduyla eşleştir” screen is open.
 */
object WirelessDebuggingHelper {

    private val latest = AtomicReference(WirelessDebugPorts())
    private var nsdManager: NsdManager? = null
    private var connectListener: NsdManager.DiscoveryListener? = null
    private var pairingListener: NsdManager.DiscoveryListener? = null
    private var localIp: String? = null

    fun isAndroid11OrAbove(): Boolean =
        Build.VERSION.SDK_INT >= Build.VERSION_CODES.R

    fun openDeveloperOptions(context: Context) {
        try {
            val intent = Intent(Settings.ACTION_APPLICATION_DEVELOPMENT_SETTINGS).apply {
                flags = Intent.FLAG_ACTIVITY_NEW_TASK
            }
            context.startActivity(intent)
        } catch (_: Exception) {
            val fallback = Intent(Settings.ACTION_SETTINGS).apply {
                flags = Intent.FLAG_ACTIVITY_NEW_TASK
            }
            context.startActivity(fallback)
        }
    }

    fun openWirelessDebugging(context: Context) {
        // No public ACTION for wireless debugging on all OEMs — developer options is best effort.
        openDeveloperOptions(context)
    }

    fun currentPorts(): WirelessDebugPorts = latest.get()

    fun refreshFromProperties(): WirelessDebugPorts {
        val connect = readIntProp(
            "service.adb.tls.port",
            "persist.adb.tls_server.port"
        )
        val pairing = readIntProp(
            "service.adb.tls.pairing.port",
            "persist.adb.wifi.pairing_port"
        )
        val enabled = readProp("persist.adb.tls_server.enable")
            ?.equals("1", ignoreCase = true)

        val prev = latest.get()
        val next = prev.copy(
            connectPort = connect ?: prev.connectPort,
            pairingPort = pairing ?: prev.pairingPort,
            wifiDebugLikelyEnabled = enabled ?: prev.wifiDebugLikelyEnabled || (connect != null),
            source = when {
                connect != null || pairing != null -> "getprop"
                else -> prev.source
            }
        )
        latest.set(next)
        return next
    }

    /** Start NSD discovery so connect/pairing ports appear when mDNS is published. */
    fun startPortDiscovery(context: Context, deviceIp: String?) {
        if (!isAndroid11OrAbove()) return
        localIp = deviceIp
        refreshFromProperties()

        val mgr = context.applicationContext.getSystemService(Context.NSD_SERVICE) as? NsdManager
            ?: return
        nsdManager = mgr

        stopPortDiscovery()

        connectListener = createListener("_adb-tls-connect._tcp") { port, host ->
            if (isLocalHost(host)) {
                latest.updateAndGet {
                    it.copy(
                        connectPort = port,
                        wifiDebugLikelyEnabled = true,
                        source = if (it.source == "getprop") "getprop+nsd" else "nsd"
                    )
                }
            }
        }
        pairingListener = createListener("_adb-tls-pairing._tcp") { port, host ->
            if (isLocalHost(host)) {
                latest.updateAndGet {
                    it.copy(
                        pairingPort = port,
                        wifiDebugLikelyEnabled = true,
                        source = if (it.source == "getprop") "getprop+nsd" else "nsd"
                    )
                }
            }
        }

        try {
            mgr.discoverServices("_adb-tls-connect._tcp", NsdManager.PROTOCOL_DNS_SD, connectListener)
        } catch (_: Exception) {
        }
        try {
            mgr.discoverServices("_adb-tls-pairing._tcp", NsdManager.PROTOCOL_DNS_SD, pairingListener)
        } catch (_: Exception) {
        }
    }

    fun stopPortDiscovery() {
        val mgr = nsdManager ?: return
        connectListener?.let {
            try {
                mgr.stopServiceDiscovery(it)
            } catch (_: Exception) {
            }
        }
        pairingListener?.let {
            try {
                mgr.stopServiceDiscovery(it)
            } catch (_: Exception) {
            }
        }
        connectListener = null
        pairingListener = null
    }

    @Deprecated("Use currentPorts().connectPort", ReplaceWith("currentPorts().connectPort"))
    fun getWirelessAdbPort(): Int? = refreshFromProperties().connectPort ?: currentPorts().connectPort

    fun getSetupGuide(): List<String> = if (isAndroid11OrAbove()) {
        listOf(
            "Kablosuz hata ayıklamayı AÇIN — Companion portları otomatik okumaya çalışır",
            "Bağlantı portu burada görünürse onu PC’de kullanın (5555 değil)",
            "İlk kez: «Eşleştirme koduyla eşleştir» → 6 haneli kod (eşleştirme portu o an görünür)",
            "PC’de Otomatik tarama / mDNS veya Manuel IP bu portlarla",
            "Port okunamazsa OEM kısıtı olabilir — ayarlardaki IP:PORT’u elle girin"
        )
    } else {
        listOf(
            "Ayarlar → Geliştirici seçenekleri",
            "USB hata ayıklamayı açın",
            "Companion uygulamasını açık tutun",
            "PC’de WiFi Bağlan → Otomatik veya Manuel"
        )
    }

    private fun isLocalHost(host: String?): Boolean {
        if (host.isNullOrBlank()) return true
        val ip = localIp ?: return true
        return host.contains(ip) || host.equals("localhost", true) || host.endsWith(".local", true)
    }

    private fun createListener(
        type: String,
        onPort: (Int, String?) -> Unit
    ): NsdManager.DiscoveryListener {
        return object : NsdManager.DiscoveryListener {
            override fun onStartDiscoveryFailed(serviceType: String?, errorCode: Int) {}
            override fun onStopDiscoveryFailed(serviceType: String?, errorCode: Int) {}
            override fun onDiscoveryStarted(serviceType: String?) {}
            override fun onDiscoveryStopped(serviceType: String?) {}
            override fun onServiceLost(serviceInfo: NsdServiceInfo?) {
                // Keep last known ports; pairing service disappearing is normal after pair UI closes.
            }

            override fun onServiceFound(serviceInfo: NsdServiceInfo?) {
                val info = serviceInfo ?: return
                val mgr = nsdManager ?: return
                try {
                    mgr.resolveService(info, object : NsdManager.ResolveListener {
                        override fun onResolveFailed(serviceInfo: NsdServiceInfo?, errorCode: Int) {}
                        override fun onServiceResolved(resolved: NsdServiceInfo?) {
                            val port = resolved?.port?.takeIf { it > 0 } ?: return
                            val host = resolved.host?.hostAddress
                            onPort(port, host)
                        }
                    })
                } catch (_: Exception) {
                }
            }
        }
    }

    private fun readIntProp(vararg keys: String): Int? {
        for (key in keys) {
            val v = readProp(key)?.toIntOrNull()?.takeIf { it > 0 }
            if (v != null) return v
        }
        return null
    }

    private fun readProp(key: String): String? {
        readPropViaSu(key)?.let { return it }
        return readPropViaGetprop(key)
    }

    private fun readPropViaSu(key: String): String? {
        return try {
            val process = Runtime.getRuntime().exec(arrayOf("su", "-c", "getprop $key"))
            val line = process.inputStream.bufferedReader().readLine()?.trim().orEmpty()
            process.waitFor()
            line.takeIf { it.isNotEmpty() && it != "0" && !it.equals("null", true) }
        } catch (_: Exception) {
            null
        }
    }

    private fun readPropViaGetprop(key: String): String? {
        return try {
            val process = Runtime.getRuntime().exec(arrayOf("getprop", key))
            val line = process.inputStream.bufferedReader().readLine()?.trim().orEmpty()
            process.waitFor()
            line.takeIf { it.isNotEmpty() && it != "0" && !it.equals("null", true) }
        } catch (_: Exception) {
            null
        }
    }
}

data class WirelessDebugPorts(
    val connectPort: Int? = null,
    val pairingPort: Int? = null,
    val wifiDebugLikelyEnabled: Boolean = false,
    val source: String = "none"
)
