package com.androidmanager.companion

import com.androidmanager.companion.BuildConfig
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import org.json.JSONObject
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.NetworkInterface
import java.net.ServerSocket
import java.net.Socket
import java.util.concurrent.atomic.AtomicReference

class DiscoveryServer(
    private val adbManager: AdbWifiManager,
    private val networkMonitor: NetworkMonitor
) {

    companion object {
        const val BROADCAST_PORT = 37020
        const val TCP_PORT = 37021
        const val BROADCAST_INTERVAL_MS = 2000L
    }

    private var udpJob: Job? = null
    private var tcpJob: Job? = null
    private var running = false
    private val pairCodeRef = AtomicReference("000000")

    fun updatePairCode(code: String) {
        pairCodeRef.set(code)
    }

    fun startBroadcast(scope: CoroutineScope) {
        running = true
        udpJob?.cancel()
        udpJob = scope.launch(Dispatchers.IO) {
            DatagramSocket().use { socket ->
                socket.broadcast = true
                socket.reuseAddress = true
                while (running && isActive) {
                    try {
                        val info = adbManager.getConnectionInfo()
                        val network = networkMonitor.currentState()
                        val payload = buildPayload(info, network).toString().toByteArray(Charsets.UTF_8)
                        for (target in resolveBroadcastTargets()) {
                            try {
                                val packet = DatagramPacket(
                                    payload,
                                    payload.size,
                                    target,
                                    BROADCAST_PORT
                                )
                                socket.send(packet)
                            } catch (_: Exception) {
                            }
                        }
                    } catch (e: Exception) {
                        e.printStackTrace()
                    }
                    delay(BROADCAST_INTERVAL_MS)
                }
            }
        }
    }

    /** 255.255.255.255 + her arayüzün subnet broadcast adresi */
    private fun resolveBroadcastTargets(): List<InetAddress> {
        val targets = linkedSetOf<InetAddress>()
        try {
            targets.add(InetAddress.getByName("255.255.255.255"))
        } catch (_: Exception) {
        }

        try {
            NetworkInterface.getNetworkInterfaces()?.toList()?.forEach { nif ->
                if (!nif.isUp || nif.isLoopback) return@forEach
                nif.interfaceAddresses.forEach { ia ->
                    ia.broadcast?.let { targets.add(it) }
                }
            }
        } catch (_: Exception) {
        }

        // Son çare: IP'den /24 broadcast üret
        adbManager.getLocalIpAddress()?.let { ip ->
            val parts = ip.split('.')
            if (parts.size == 4) {
                try {
                    targets.add(InetAddress.getByName("${parts[0]}.${parts[1]}.${parts[2]}.255"))
                } catch (_: Exception) {
                }
            }
        }

        return targets.toList()
    }

    fun startTcpServer(scope: CoroutineScope, onPcConnected: (String) -> Unit) {
        tcpJob?.cancel()
        tcpJob = scope.launch(Dispatchers.IO) {
            ServerSocket(TCP_PORT).use { server ->
                server.reuseAddress = true
                while (running && isActive) {
                    try {
                        val client = server.accept()
                        handleClient(client, onPcConnected)
                    } catch (e: Exception) {
                        if (running) e.printStackTrace()
                    }
                }
            }
        }
    }

    private fun handleClient(socket: Socket, onPcConnected: (String) -> Unit) {
        socket.use {
            val reader = socket.getInputStream().bufferedReader()
            val writer = socket.getOutputStream().bufferedWriter()
            val received = reader.readLine()?.trim() ?: return
            val json = JSONObject(received)
            val code = json.optString("pairCode")
            val pcName = json.optString("pcName", "PC")
            val expected = pairCodeRef.get()

            val response = if (code == expected) {
                onPcConnected(pcName)
                val info = adbManager.getConnectionInfo()
                JSONObject().apply {
                    put("status", "approved")
                    put("ip", info.ipAddress)
                    put("port", info.adbPort)
                    put("device", info.deviceName)
                    put("deviceId", info.deviceId)
                    info.wirelessConnectPort?.let { put("wirelessAdbPort", it) }
                    info.wirelessPairingPort?.let { put("wirelessPairingPort", it) }
                    put("wirelessDebugEnabled", info.wirelessDebugEnabled)
                }
            } else {
                JSONObject().apply {
                    put("status", "rejected")
                    put("reason", "Gecersiz pair code")
                }
            }
            writer.write(response.toString())
            writer.newLine()
            writer.flush()
        }
    }

    private fun buildPayload(info: ConnectionInfo, network: NetworkState): JSONObject = JSONObject().apply {
        put("type", "AndroidManagerCompanion")
        put("deviceId", info.deviceId)
        put("ipAddress", info.ipAddress)
        put("adbPort", info.adbPort)
        put("deviceName", info.deviceName)
        put("deviceModel", info.deviceModel)
        put("androidVersion", info.androidVersion)
        put("sdkVersion", info.sdkVersion)
        put("tcpPort", TCP_PORT)
        put("timestamp", System.currentTimeMillis())
        info.wirelessConnectPort?.let { put("wirelessAdbPort", it) }
        info.wirelessPairingPort?.let { put("wirelessPairingPort", it) }
        put("wirelessDebugEnabled", info.wirelessDebugEnabled)
        put("wirelessPortSource", info.wirelessPortSource)
        put("portSource", info.wirelessPortSource)
        put("networkType", network.networkType)
        put("networkGeneration", network.generation)
        put("manufacturer", android.os.Build.MANUFACTURER)
        put("companionVersion", BuildConfig.VERSION_NAME)
        put("capabilities", org.json.JSONArray(listOf("discovery", "pairing", "presence")))
    }

    fun stop() {
        running = false
        udpJob?.cancel()
        tcpJob?.cancel()
    }
}
