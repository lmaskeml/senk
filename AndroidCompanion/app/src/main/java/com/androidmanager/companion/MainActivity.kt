package com.androidmanager.companion

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.os.Bundle
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.lifecycle.lifecycleScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive

class MainActivity : ComponentActivity() {

    private lateinit var adbManager: AdbWifiManager
    private lateinit var discoveryServer: DiscoveryServer

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        adbManager = AdbWifiManager(this)
        val networkMonitor = NetworkMonitor(this)
        discoveryServer = DiscoveryServer(adbManager, networkMonitor)

        val initialCode = PairCodeManager.generate()
        discoveryServer.updatePairCode(initialCode)

        setContent {
            MaterialTheme {
                CompanionScreen(initialPairCode = initialCode)
            }
        }
    }

    @Composable
    private fun CompanionScreen(initialPairCode: String) {
        val infoState = remember { mutableStateOf(adbManager.getConnectionInfo()) }
        val pairCode = remember { mutableStateOf(initialPairCode) }
        val connectedPc = remember { mutableStateOf<String?>(null) }
        val isConnected = connectedPc.value != null
        val info = infoState.value
        val qrBitmap = remember(pairCode.value, info.ipAddress, info.adbPort) {
            QRGenerator.generateQR(info, pairCode.value)
        }

        DisposableEffect(Unit) {
            WirelessDebuggingHelper.startPortDiscovery(this@MainActivity, adbManager.getLocalIpAddress())
            onDispose { WirelessDebuggingHelper.stopPortDiscovery() }
        }

        LaunchedEffect(Unit) {
            discoveryServer.startBroadcast(lifecycleScope)
            discoveryServer.startTcpServer(lifecycleScope) { pcName ->
                runOnUiThread {
                    connectedPc.value = pcName
                    val msg = if (WirelessDebuggingHelper.isAndroid11OrAbove()) {
                        "$pcName eşleşme kodunu kabul etti. ADB için Kablosuz hata ayıklama portları gerekli."
                    } else {
                        "$pcName bağlandı!"
                    }
                    Toast.makeText(this@MainActivity, msg, Toast.LENGTH_LONG).show()
                }
            }
        }

        // Refresh wireless ports into UI every 1.5s; re-arm NSD if connect port still unknown.
        LaunchedEffect(Unit) {
            var ticks = 0
            while (isActive) {
                infoState.value = adbManager.getConnectionInfo()
                ticks++
                if (ticks % 10 == 0
                    && WirelessDebuggingHelper.isAndroid11OrAbove()
                    && WirelessDebuggingHelper.currentPorts().connectPort == null
                ) {
                    WirelessDebuggingHelper.startPortDiscovery(
                        this@MainActivity,
                        adbManager.getLocalIpAddress()
                    )
                }
                delay(1500)
            }
        }

        Column(
            modifier = Modifier
                .fillMaxSize()
                .background(Color(0xFF1E1E2E))
                .padding(24.dp)
                .verticalScroll(rememberScrollState()),
            horizontalAlignment = Alignment.CenterHorizontally
        ) {
            Text(
                text = "SeND ANDROID MANAGER",
                color = Color.White,
                fontSize = 24.sp,
                fontWeight = FontWeight.Bold
            )
            Text(text = "Companion", color = Color(0xFF888888), fontSize = 14.sp)

            Spacer(modifier = Modifier.height(20.dp))
            StatusCard(isConnected, connectedPc.value)
            Spacer(modifier = Modifier.height(12.dp))
            InfoCard(info)
            Spacer(modifier = Modifier.height(12.dp))

            if (WirelessDebuggingHelper.isAndroid11OrAbove()) {
                WirelessPortsCard(
                    info = info,
                    onRefresh = { infoState.value = adbManager.getConnectionInfo() },
                    onCopyConnect = {
                        val port = info.wirelessConnectPort
                        if (port != null) {
                            copyToClipboard("${info.ipAddress}:$port")
                        } else {
                            Toast.makeText(
                                this@MainActivity,
                                "Bağlantı portu henüz okunamadı — kablosuz hata ayıklamayı açın",
                                Toast.LENGTH_SHORT
                            ).show()
                        }
                    },
                    onOpenSettings = { WirelessDebuggingHelper.openWirelessDebugging(this@MainActivity) }
                )
                Spacer(modifier = Modifier.height(12.dp))
            }

            if (!isConnected) {
                QRCard(
                    bitmap = qrBitmap,
                    pairCode = pairCode.value,
                    onRefresh = {
                        val next = PairCodeManager.generate()
                        pairCode.value = next
                        discoveryServer.updatePairCode(next)
                    }
                )
            }

            if (WirelessDebuggingHelper.isAndroid11OrAbove()) {
                Spacer(modifier = Modifier.height(12.dp))
                Android11Guide()
            }

            Spacer(modifier = Modifier.height(12.dp))
            OutlinedButton(
                onClick = {
                    OemAutoStartHelper.requestBatteryOptimizationExemption(this@MainActivity)
                    OemAutoStartHelper.openAutoStartSettings(this@MainActivity)
                },
                modifier = Modifier.fillMaxWidth().height(48.dp),
                shape = RoundedCornerShape(12.dp)
            ) {
                Text(
                    "OEM arka plan izinleri (${OemAutoStartHelper.oemLabel()})",
                    color = Color(0xFFCCCCCC),
                    fontSize = 13.sp
                )
            }

            Spacer(modifier = Modifier.height(20.dp))
            if (WirelessDebuggingHelper.isAndroid11OrAbove()) {
                OutlinedButton(
                    onClick = { WirelessDebuggingHelper.openWirelessDebugging(this@MainActivity) },
                    modifier = Modifier.fillMaxWidth().height(52.dp),
                    shape = RoundedCornerShape(12.dp)
                ) {
                    Text("Kablosuz hata ayıklamayı aç", color = Color.White, fontSize = 15.sp)
                }
            } else {
                Button(
                    onClick = { copyToClipboard("${info.ipAddress}:${info.legacyAdbPort}") },
                    modifier = Modifier.fillMaxWidth().height(52.dp),
                    shape = RoundedCornerShape(12.dp),
                    colors = ButtonDefaults.buttonColors(containerColor = Color(0xFF4CAF50))
                ) {
                    Text(
                        text = "${info.ipAddress}:${info.legacyAdbPort}  Kopyala",
                        fontSize = 16.sp,
                        fontWeight = FontWeight.Medium
                    )
                }
            }
        }
    }

    @Composable
    private fun WirelessPortsCard(
        info: ConnectionInfo,
        onRefresh: () -> Unit,
        onCopyConnect: () -> Unit,
        onOpenSettings: () -> Unit
    ) {
        val connectText = info.wirelessConnectPort?.toString() ?: "okunamadı"
        val pairingText = info.wirelessPairingPort?.toString()
            ?: "eşleştirme ekranı açık değil"
        val statusColor = when {
            info.wirelessConnectPort != null -> Color(0xFF1B5E20)
            else -> Color(0xFF4E342E)
        }

        Card(
            modifier = Modifier.fillMaxWidth(),
            shape = RoundedCornerShape(12.dp),
            colors = CardDefaults.cardColors(containerColor = statusColor)
        ) {
            Column(Modifier.padding(16.dp)) {
                Text(
                    text = "Kablosuz hata ayıklama portları",
                    color = Color.White,
                    fontWeight = FontWeight.Bold
                )
                Text(
                    text = "PC’ye bunları verin (Companion 5555 değil)",
                    color = Color(0xFFFFCCBC),
                    fontSize = 11.sp,
                    modifier = Modifier.padding(top = 4.dp)
                )
                Spacer(modifier = Modifier.height(10.dp))
                InfoRow("IP", info.ipAddress)
                InfoRow("Bağlantı portu", connectText)
                InfoRow("Eşleştirme portu", pairingText)
                InfoRow("Kaynak", info.wirelessPortSource)
                Spacer(modifier = Modifier.height(10.dp))
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    Button(
                        onClick = onCopyConnect,
                        colors = ButtonDefaults.buttonColors(containerColor = Color(0xFF2E7D32))
                    ) {
                        Text("IP:port kopyala", fontSize = 12.sp)
                    }
                    OutlinedButton(onClick = onRefresh) {
                        Text("Yenile", color = Color.White, fontSize = 12.sp)
                    }
                }
                Spacer(modifier = Modifier.height(6.dp))
                OutlinedButton(onClick = onOpenSettings, modifier = Modifier.fillMaxWidth()) {
                    Text("Sistem ayarını aç", color = Color.White, fontSize = 12.sp)
                }
            }
        }
    }

    @Composable
    private fun StatusCard(isConnected: Boolean, pcName: String?) {
        Card(
            modifier = Modifier.fillMaxWidth(),
            shape = RoundedCornerShape(12.dp),
            colors = CardDefaults.cardColors(
                containerColor = if (isConnected) Color(0xFF1B5E20) else Color(0xFF2E2E3E)
            )
        ) {
            Row(Modifier.padding(16.dp), verticalAlignment = Alignment.CenterVertically) {
                Text(text = if (isConnected) "ON" else "WAIT", color = Color.White, fontWeight = FontWeight.Bold)
                Spacer(modifier = Modifier.width(12.dp))
                Column {
                    Text(
                        text = if (isConnected) {
                            if (WirelessDebuggingHelper.isAndroid11OrAbove())
                                "Kod kabul edildi (ADB değil)"
                            else
                                "Bağlı"
                        } else {
                            "Bağlantı bekleniyor"
                        },
                        color = Color.White,
                        fontWeight = FontWeight.Bold
                    )
                    if (pcName != null) {
                        Text(
                            text = if (WirelessDebuggingHelper.isAndroid11OrAbove())
                                "PC kodu OK — kablosuz debug portlarını kullanın"
                            else
                                "PC: $pcName",
                            color = Color(0xFF81C784),
                            fontSize = 12.sp
                        )
                    }
                }
            }
        }
    }

    @Composable
    private fun InfoCard(info: ConnectionInfo) {
        Card(
            modifier = Modifier.fillMaxWidth(),
            shape = RoundedCornerShape(12.dp),
            colors = CardDefaults.cardColors(containerColor = Color(0xFF2E2E3E))
        ) {
            Column(Modifier.padding(16.dp)) {
                InfoRow("IP", info.ipAddress)
                InfoRow("Cihaz", info.deviceName)
                InfoRow("Android", info.androidVersion)
            }
        }
    }

    @Composable
    private fun InfoRow(label: String, value: String) {
        Row(
            modifier = Modifier.fillMaxWidth().padding(vertical = 4.dp),
            horizontalArrangement = Arrangement.SpaceBetween
        ) {
            Text(text = label, color = Color(0xFF888888), fontSize = 13.sp)
            Text(text = value, color = Color.White, fontSize = 13.sp, fontWeight = FontWeight.Medium)
        }
    }

    @Composable
    private fun QRCard(
        bitmap: android.graphics.Bitmap,
        pairCode: String,
        onRefresh: () -> Unit
    ) {
        Card(
            modifier = Modifier.fillMaxWidth(),
            shape = RoundedCornerShape(12.dp),
            colors = CardDefaults.cardColors(containerColor = Color(0xFF2E2E3E))
        ) {
            Column(
                modifier = Modifier.padding(16.dp),
                horizontalAlignment = Alignment.CenterHorizontally
            ) {
                Text(text = "QR ile bağlan", color = Color.White, fontWeight = FontWeight.Bold)
                Spacer(modifier = Modifier.height(12.dp))
                Box(
                    modifier = Modifier
                        .background(Color.White, RoundedCornerShape(8.dp))
                        .padding(8.dp)
                ) {
                    Image(
                        bitmap = bitmap.asImageBitmap(),
                        contentDescription = "QR",
                        modifier = Modifier.size(200.dp)
                    )
                }
                Spacer(modifier = Modifier.height(12.dp))
                Text(text = "Companion Pair Code", color = Color(0xFF888888), fontSize = 12.sp)
                Text(
                    text = pairCode.chunked(2).joinToString(" "),
                    color = Color(0xFF4FC3F7),
                    fontSize = 32.sp,
                    fontWeight = FontWeight.Bold,
                    letterSpacing = 4.sp
                )
                Text(
                    text = "Android 14’te asıl ADB kodu kablosuz hata ayıklamadadır",
                    color = Color(0xFF888888),
                    fontSize = 12.sp,
                    textAlign = TextAlign.Center
                )
                Spacer(modifier = Modifier.height(12.dp))
                OutlinedButton(onClick = onRefresh) {
                    Text("Kodu yenile", color = Color.White)
                }
            }
        }
    }

    @Composable
    private fun Android11Guide() {
        val steps = remember { WirelessDebuggingHelper.getSetupGuide() }
        Card(
            modifier = Modifier.fillMaxWidth(),
            shape = RoundedCornerShape(12.dp),
            colors = CardDefaults.cardColors(containerColor = Color(0xFFB71C1C))
        ) {
            Column(Modifier.padding(16.dp)) {
                Text(
                    text = "Önemli: Android 11 / 12 / 13 / 14",
                    color = Color.White,
                    fontWeight = FontWeight.Bold
                )
                Spacer(modifier = Modifier.height(6.dp))
                Text(
                    text = "Mümkünse bağlantı portu yukarıdaki kutuda otomatik görünür. " +
                        "Bazı telefonlar (OEM) uygulamaya portu vermez; o zaman ayarlardaki IP:PORT’u kullanın.",
                    color = Color(0xFFFFCDD2),
                    fontSize = 12.sp
                )
                Spacer(modifier = Modifier.height(8.dp))
                steps.forEach { step ->
                    Text(
                        text = "• $step",
                        color = Color(0xFFFFCDD2),
                        fontSize = 12.sp,
                        modifier = Modifier.padding(vertical = 2.dp)
                    )
                }
            }
        }
    }

    private fun copyToClipboard(text: String) {
        val clipboard = getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
        clipboard.setPrimaryClip(ClipData.newPlainText("ADB", text))
        Toast.makeText(this, "Kopyalandı: $text", Toast.LENGTH_SHORT).show()
    }

    override fun onDestroy() {
        WirelessDebuggingHelper.stopPortDiscovery()
        discoveryServer.stop()
        super.onDestroy()
    }
}
