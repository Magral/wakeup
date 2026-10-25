package ru.wakemini.app;

import android.content.Context;
import android.content.SharedPreferences;

import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetAddress;
import java.net.SocketTimeoutException;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.security.SecureRandom;
import java.util.Locale;
import javax.crypto.Mac;
import javax.crypto.spec.SecretKeySpec;

final class WakeController {
    static final String PREFS = "wake";
    private static final SecureRandom RANDOM = new SecureRandom();
    private static final int TIMEOUT_MS = 1800;

    private WakeController() {}

    static String validate(String macText, String host, String portText, String keyText) {
        String mac = normalizeMac(macText);
        if (mac == null) return "Ошибка: проверьте MAC-адрес (нужно 12 шестнадцатеричных знаков).";
        if (!isIpv4(host) && !isHostname(host)) return "Ошибка: укажите IPv4-адрес или DDNS-имя.";
        if (isIpv4(host) && !validIpv4(host)) return "Ошибка: в IPv4-адресе каждая часть должна быть от 0 до 255.";
        try {
            int port = Integer.parseInt(portText);
            if (port < 1 || port > 65535) throw new NumberFormatException();
            if (!keyText.isEmpty() && port != 3000) return "Ошибка: для помощника Windows установите UDP-порт 3000.";
        } catch (Exception ex) {
            return "Ошибка: UDP-порт должен быть от 1 до 65535.";
        }
        if (!keyText.isEmpty() && hexToBytes(keyText) == null) return "Ошибка: ключ должен состоять из 64 hex-символов.";
        return null;
    }

    static String toggle(Context context) {
        SharedPreferences p = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE);
        String macText = p.getString("mac", "").trim();
        String host = p.getString("address", "").trim();
        String portText = p.getString("port", "3000").trim();
        String keyText = p.getString("key", "").trim();
        String issue = validate(macText, host, portText, keyText);
        if (issue != null) return issue;
        int port = Integer.parseInt(portText);
        try {
            InetAddress target = resolveIpv4(host);
            byte[] key = keyText.isEmpty() ? null : hexToBytes(keyText);
            if (key == null) {
                sendMagicPacket(target, port, macText);
                return "Пакет пробуждения отправлен.";
            }
            return toggleWithAgent(target, port, macText, key);
        } catch (SocketTimeoutException ex) {
            return "ПК не ответил. Отправлен пакет включения.";
        } catch (Exception ex) {
            return "Ошибка отправки: " + (ex.getMessage() == null ? "проверьте адрес и сеть." : ex.getMessage());
        }
    }

    private static String toggleWithAgent(InetAddress target, int port, String macText, byte[] key) throws Exception {
        byte[] random = new byte[16];
        RANDOM.nextBytes(random);
        String nonce = toHex(random);
        try (DatagramSocket socket = new DatagramSocket()) {
            socket.setBroadcast(true);
            socket.setSoTimeout(TIMEOUT_MS);
            sendText(socket, target, port, "WM1|PING|" + nonce);
            String response;
            try {
                response = receiveText(socket, target);
            } catch (SocketTimeoutException ex) {
                sendMagicPacket(target, port, macText);
                return "ПК не ответил. Отправлен пакет включения.";
            }
            String[] fields = response.split("\\|", -1);
            if (fields.length != 4 || !fields[0].equals("WM1") || !fields[1].equals("ONLINE") || !fields[2].equals(nonce)
                    || !verify(key, "ONLINE|" + nonce, fields[3])) {
                return "Ошибка: помощник ответил, но ключ не совпадает.";
            }
            sendText(socket, target, port, "WM1|SHUTDOWN|" + nonce + "|" + sign(key, "SHUTDOWN|" + nonce));
            String acknowledgement;
            try {
                acknowledgement = receiveText(socket, target);
            } catch (SocketTimeoutException ex) {
                return "Ошибка: помощник не подтвердил выключение. ПК мог принять команду.";
            }
            String[] ack = acknowledgement.split("\\|", -1);
            if (ack.length == 4 && ack[0].equals("WM1") && ack[1].equals("SHUTDOWN_ACCEPTED") && ack[2].equals(nonce)
                    && verify(key, "SHUTDOWN_ACCEPTED|" + nonce, ack[3])) {
                return "Команда выключения принята. ПК выключится через 10 секунд.";
            }
            return "Ошибка: не получено подтверждение выключения.";
        }
    }

    private static void sendMagicPacket(InetAddress target, int port, String macText) throws Exception {
        String normalized = normalizeMac(macText);
        if (normalized == null) throw new IllegalArgumentException("неверный MAC-адрес");
        byte[] mac = hexToBytes(normalized);
        byte[] packet = new byte[6 + 16 * 6];
        for (int i = 0; i < 6; i++) packet[i] = (byte) 0xff;
        for (int repeat = 0; repeat < 16; repeat++) System.arraycopy(mac, 0, packet, 6 + repeat * 6, 6);
        try (DatagramSocket socket = new DatagramSocket()) {
            socket.setBroadcast(true);
            socket.send(new DatagramPacket(packet, packet.length, target, port));
        }
    }

    private static void sendText(DatagramSocket socket, InetAddress target, int port, String text) throws Exception {
        byte[] bytes = text.getBytes(StandardCharsets.UTF_8);
        socket.send(new DatagramPacket(bytes, bytes.length, target, port));
    }

    private static String receiveText(DatagramSocket socket, InetAddress expectedSource) throws Exception {
        byte[] buffer = new byte[256];
        DatagramPacket packet = new DatagramPacket(buffer, buffer.length);
        long deadline = System.currentTimeMillis() + TIMEOUT_MS;
        while (true) {
            long left = deadline - System.currentTimeMillis();
            if (left <= 0) throw new SocketTimeoutException();
            socket.setSoTimeout((int) left);
            socket.receive(packet);
            if (!packet.getAddress().equals(expectedSource)) continue;
            return new String(packet.getData(), packet.getOffset(), packet.getLength(), StandardCharsets.UTF_8);
        }
    }

    private static InetAddress resolveIpv4(String host) throws Exception {
        for (InetAddress address : InetAddress.getAllByName(host)) {
            if (address instanceof java.net.Inet4Address) return address;
        }
        throw new IllegalArgumentException("имя не разрешилось в IPv4-адрес");
    }

    private static String normalizeMac(String value) {
        String mac = value.replace(":", "").replace("-", "").replace(" ", "");
        return mac.matches("(?i)[0-9a-f]{12}") ? mac.toUpperCase(Locale.ROOT) : null;
    }

    private static boolean isIpv4(String host) { return host.matches("(?:[0-9]{1,3}\\.){3}[0-9]{1,3}"); }
    private static boolean validIpv4(String host) {
        try { for (String p : host.split("\\.")) if (Integer.parseInt(p) > 255) return false; return true; }
        catch (Exception ex) { return false; }
    }
    private static boolean isHostname(String host) {
        return host.length() <= 253 && host.matches("(?i)(?=.{1,253}$)(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)(?:\\.(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?))*\\.?");
    }
    private static String sign(byte[] key, String message) throws Exception {
        Mac mac = Mac.getInstance("HmacSHA256");
        mac.init(new SecretKeySpec(key, "HmacSHA256"));
        return toHex(mac.doFinal(message.getBytes(StandardCharsets.UTF_8)));
    }
    private static boolean verify(byte[] key, String message, String supplied) {
        try { return MessageDigest.isEqual(hexToBytes(sign(key, message)), hexToBytes(supplied)); }
        catch (Exception ex) { return false; }
    }
    private static byte[] hexToBytes(String value) {
        if (value == null || value.isEmpty() || (value.length() & 1) != 0 || !value.matches("(?i)[0-9a-f]+")) return null;
        byte[] result = new byte[value.length() / 2];
        for (int i = 0; i < result.length; i++) result[i] = (byte) Integer.parseInt(value.substring(i * 2, i * 2 + 2), 16);
        return result;
    }
    private static String toHex(byte[] value) {
        StringBuilder out = new StringBuilder(value.length * 2);
        for (byte b : value) out.append(String.format(Locale.ROOT, "%02x", b & 0xff));
        return out.toString();
    }
}
