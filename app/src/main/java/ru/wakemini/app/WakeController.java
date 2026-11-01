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
        if (!keyText.isEmpty() && (keyText.length() != 64 || hexToBytes(keyText) == null)) return "Ошибка: ключ должен состоять из 64 hex-символов.";
        return null;
    }

    static final class Status {
        final boolean online;
        final String message;
        Status(boolean online, String message) { this.online = online; this.message = message; }
    }

    private static final class Session implements AutoCloseable {
        final DatagramSocket socket;
        final InetAddress address;
        final String nonce;
        final int port;
        Session(InetAddress address, int port) throws Exception {
            this.address = address; this.port = port;
            nonce = toHex(randomBytes(16));
            socket = new DatagramSocket();
            socket.connect(address, port);
        }
        public void close() { socket.close(); }
    }

    private static byte[] randomBytes(int count) {
        byte[] bytes = new byte[count]; RANDOM.nextBytes(bytes); return bytes;
    }

    private static Session connect(InetAddress address, int port, byte[] key) throws Exception {
        for (int attempt = 0; attempt < 2; attempt++) {
            Session session = new Session(address, port);
            try {
                sendText(session.socket, address, port, "WM1|PING|" + session.nonce);
                String reply = receiveText(session.socket, address);
                if (!validReply(reply, "ONLINE", session.nonce, key))
                    throw new SecurityException("Помощник ответил, но ключ не совпадает.");
                return session;
            } catch (SocketTimeoutException ex) {
                session.close();
            } catch (Exception ex) { session.close(); throw ex; }
        }
        throw new SocketTimeoutException();
    }

    static Status diagnose(Context context) {
        SharedPreferences p = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE);
        try {
            String issue = validate(p.getString("mac", ""), p.getString("address", ""), p.getString("port", "3000"), p.getString("key", ""));
            if (issue != null) return new Status(false, issue);
            byte[] key = hexToBytes(p.getString("key", ""));
            if (key == null) return new Status(false, "Добавьте ключ Windows-помощника.");
            long start = android.os.SystemClock.elapsedRealtime();
            try (Session session = connect(resolveIpv4(p.getString("address", "")), Integer.parseInt(p.getString("port", "3000")), key)) {
                return new Status(true, "ПК включён · " + (android.os.SystemClock.elapsedRealtime() - start) + " мс");
            }
        } catch (java.net.UnknownHostException ex) { return new Status(false, "Ошибка: DDNS/IP не найден. Проверьте адрес и интернет."); }
        catch (SecurityException ex) { return new Status(false, "Ошибка: ключ помощника не совпадает."); }
        catch (SocketTimeoutException ex) { return new Status(false, "ПК выключен или помощник не отвечает. Проверьте UDP 3000 и Брандмауэр."); }
        catch (Exception ex) { return new Status(false, "Ошибка: сеть или UDP-порт недоступны."); }
    }

    static boolean isAgentOnline(Context context) { return diagnose(context).online; }
    static String toggle(Context context) { return execute(context, "TOGGLE"); }

    static String execute(Context context, String action) {
        if (!java.util.Arrays.asList("ON", "ON_TV", "TV_PC", "TV_STATUS", "TV_CANCEL", "TOGGLE", "SHUTDOWN", "RESTART", "SLEEP", "PROFILE_WORK", "PROFILE_GAME", "PROFILE_CINEMA").contains(action))
            return "Ошибка: неизвестная команда.";
        SharedPreferences p = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE);
        String mac = p.getString("mac", "").trim();
        String host = p.getString("address", p.getString("broadcast", "")).trim();
        String portText = p.getString("port", "3000").trim();
        String keyText = p.getString("key", "").trim();
        String issue = validate(mac, host, portText, keyText);
        if (issue != null) return issue;
        try {
            InetAddress target = resolveIpv4(host);
            int port = Integer.parseInt(portText);
            byte[] key = hexToBytes(keyText);
            if (action.equals("ON_TV")) {
                if (key == null) return "Ошибка: добавьте ключ Windows-помощника.";
                return wakeWithTv(target, port, mac, key);
            }
            if (action.equals("ON") || (action.equals("TOGGLE") && key == null)) {
                sendMagicPacket(target, port, mac); return "Пакет пробуждения отправлен.";
            }
            if (key == null) return "Ошибка: добавьте ключ Windows-помощника.";
            Session session;
            try { session = connect(target, port, key); }
            catch (SocketTimeoutException ex) {
                if (!action.equals("TOGGLE")) return "Ошибка: помощник не ответил. Команда не отправлена.";
                sendMagicPacket(target, port, mac); return "ПК не ответил. Отправлен пакет включения.";
            }
            try (Session active = session) {
                String command = action.equals("TOGGLE") ? "SHUTDOWN" : action;
                String reply = commandReply(active, key, command);
                if (reply.equals(command + "_ACCEPTED"))
                    return command.equals("SHUTDOWN") ? "Команда выключения принята. ПК выключится через 10 секунд."
                            : command.equals("TV_CANCEL") ? "Запрошена остановка попыток ТВ. Уже выполненное переключение не отменяется."
                            : command.equals("TV_PC") ? "Запрос ТВ принят. Результат — в диагностике помощника."
                            : "Команда принята: " + command;
                if (reply.endsWith("_NOT_CONFIGURED")) return "Ошибка: настройте ТВ или профиль в помощнике на ПК.";
                if (reply.equals("TV_PC_BUSY")) return "Управление телевизором уже выполняется.";
                if (reply.equals("TV_STATUS_OK")) return "Телевизор подтвердил переключение HDMI.";
                if (reply.equals("TV_STATUS_BUSY")) return "Телевизор ещё включается.";
                if (reply.equals("TV_STATUS_IDLE")) return "Команд телевизору ещё не было после запуска помощника.";
                return "Ошибка: команда не выполнена. Проверьте диагностику помощника.";
            }
        } catch (SecurityException ex) { return "Ошибка: помощник ответил, но ключ не совпадает."; }
        catch (java.net.UnknownHostException ex) { return "Ошибка: DDNS/IP не найден."; }
        catch (InterruptedException ex) { Thread.currentThread().interrupt(); return "Ожидание отменено. Уже принятая команда ТВ могла выполниться."; }
        catch (java.io.IOException ex) { return "Ошибка: " + ex.getMessage(); }
        catch (Exception ex) { return "Ошибка: сеть или порт недоступны."; }
    }

    private static String commandReply(Session session, byte[] key, String command) throws Exception {
        String packet = "WM1|" + command + "|" + session.nonce + "|" + sign(key, command + "|" + session.nonce);
        for (int attempt = 0; attempt < 2; attempt++) {
            if (Thread.currentThread().isInterrupted()) throw new InterruptedException();
            sendText(session.socket, session.address, session.port, packet);
            try {
                String reply = receiveText(session.socket, session.address);
                for (String suffix : new String[]{"ACCEPTED", "NOT_CONFIGURED", "FAILED", "BUSY", "OK", "IDLE"}) {
                    String verb = command + "_" + suffix;
                    if (validReply(reply, verb, session.nonce, key)) return verb;
                }
                throw new SecurityException("Неверная подпись подтверждения.");
            } catch (SocketTimeoutException ignored) { }
        }
        throw new java.io.IOException("Нет подтверждения: команда могла выполниться.");
    }

    private static String wakeWithTv(InetAddress target, int port, String mac, byte[] key) throws Exception {
        // The PC must first boot and start its user-session helper. No TV credentials leave the PC.
        Session session = null;
        try { session = connect(target, port, key); }
        catch (SocketTimeoutException | java.net.PortUnreachableException offline) { sendMagicPacket(target, port, mac); }
        long deadline = android.os.SystemClock.elapsedRealtime() + 120_000;
        while (session == null && android.os.SystemClock.elapsedRealtime() < deadline) {
            Thread.sleep(2000);
            try { session = connect(target, port, key); }
            catch (SocketTimeoutException | java.net.PortUnreachableException offline) { }
        }
        if (session == null) return "Ошибка: ПК не запустил помощник за 2 минуты. Войдите в Windows и повторите команду.";
        try (Session ready = session) {
            String reply = commandReply(ready, key, "TV_PC");
            if (reply.equals("TV_PC_NOT_CONFIGURED")) return "Ошибка: выполните сопряжение LG TV в помощнике Windows 1.4.0.";
            if (!reply.equals("TV_PC_ACCEPTED") && !reply.equals("TV_PC_BUSY")) return "Ошибка: помощник не принял управление ТВ.";
        }
        deadline = android.os.SystemClock.elapsedRealtime() + 70_000;
        while (android.os.SystemClock.elapsedRealtime() < deadline) {
            Thread.sleep(2000);
            try (Session status = connect(target, port, key)) {
                String reply = commandReply(status, key, "TV_STATUS");
                if (reply.equals("TV_STATUS_OK")) return "Телевизор подтвердил HDMI компьютера.";
                if (!reply.equals("TV_STATUS_BUSY")) return "Ошибка: телевизор не подтвердил переключение. Проверьте диагностику на ПК.";
            }
        }
        return "Ошибка: истекло ожидание ТВ. Команда могла выполниться; проверьте экран и диагностику.";
    }

    private static boolean validReply(String reply, String verb, String nonce, byte[] key) {
        String[] parts = reply.split("\\|", -1);
        return parts.length == 4 && parts[0].equals("WM1") && parts[1].equals(verb)
                && parts[2].equals(nonce) && verify(key, verb + "|" + nonce, parts[3]);
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
