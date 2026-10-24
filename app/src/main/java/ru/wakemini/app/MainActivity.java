package ru.wakemini.app;

import android.app.Activity;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.os.Bundle;
import android.text.InputType;
import android.view.Gravity;
import android.view.View;
import android.view.inputmethod.InputMethodManager;
import android.content.Context;
import android.widget.Button;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;

import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetAddress;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

public class MainActivity extends Activity {
    private static final int BG = Color.rgb(16, 24, 32);
    private static final int PANEL = Color.rgb(25, 36, 46);
    private static final int TEXT = Color.rgb(240, 245, 248);
    private static final int MUTED = Color.rgb(157, 173, 184);
    private static final int ACCENT = Color.rgb(39, 198, 183);
    private final ExecutorService worker = Executors.newSingleThreadExecutor();
    private EditText macInput;
    private EditText broadcastInput;
    private EditText portInput;
    private Button wakeButton;
    private TextView statusView;

    @Override
    protected void onCreate(Bundle state) {
        super.onCreate(state);
        getWindow().setStatusBarColor(BG);
        getWindow().setNavigationBarColor(BG);
        getWindow().getDecorView().setSystemUiVisibility(0);
        buildScreen();
        loadSaved();
    }

    private void buildScreen() {
        ScrollView scroll = new ScrollView(this);
        scroll.setFillViewport(true);
        scroll.setBackgroundColor(BG);
        LinearLayout page = new LinearLayout(this);
        page.setOrientation(LinearLayout.VERTICAL);
        page.setPadding(dp(22), dp(24), dp(22), dp(22));
        scroll.addView(page);

        TextView eyebrow = text("WAKE ON LAN", 12, ACCENT, true);
        eyebrow.setLetterSpacing(0.12f);
        page.addView(eyebrow);
        TextView title = text("Разбудить ПК", 30, TEXT, true);
        LinearLayout.LayoutParams titleParams = params(-1, -2);
        titleParams.topMargin = dp(7);
        page.addView(title, titleParams);
        TextView intro = text("Отправьте magic packet в локальную сеть.", 15, MUTED, false);
        LinearLayout.LayoutParams introParams = params(-1, -2);
        introParams.topMargin = dp(6);
        page.addView(intro, introParams);

        LinearLayout card = new LinearLayout(this);
        card.setOrientation(LinearLayout.VERTICAL);
        card.setPadding(dp(16), dp(17), dp(16), dp(17));
        card.setBackground(round(PANEL, dp(18)));
        LinearLayout.LayoutParams cardParams = params(-1, -2);
        cardParams.topMargin = dp(25);
        page.addView(card, cardParams);

        addLabel(card, "MAC-АДРЕС ПК");
        macInput = input("Например, 00:11:22:AA:BB:CC", InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS);
        LinearLayout.LayoutParams inputParams = params(-1, dp(54));
        inputParams.topMargin = dp(8);
        card.addView(macInput, inputParams);

        addLabel(card, "АДРЕС НАЗНАЧЕНИЯ / DDNS", dp(18));
        broadcastInput = input("255.255.255.255 или home.example.net", InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS);
        LinearLayout.LayoutParams bcParams = params(-1, dp(54));
        bcParams.topMargin = dp(8);
        card.addView(broadcastInput, bcParams);

        addLabel(card, "UDP-ПОРТ", dp(18));
        portInput = input("9", InputType.TYPE_CLASS_NUMBER);
        LinearLayout.LayoutParams portParams = params(-1, dp(54));
        portParams.topMargin = dp(8);
        card.addView(portInput, portParams);

        wakeButton = new Button(this);
        wakeButton.setText("⏻   РАЗБУДИТЬ ПК");
        wakeButton.setTextColor(Color.rgb(9, 28, 30));
        wakeButton.setTextSize(15);
        wakeButton.setTypeface(Typeface.DEFAULT, Typeface.BOLD);
        wakeButton.setAllCaps(false);
        wakeButton.setBackground(round(ACCENT, dp(16)));
        wakeButton.setPadding(dp(12), 0, dp(12), 0);
        LinearLayout.LayoutParams buttonParams = params(-1, dp(58));
        buttonParams.topMargin = dp(20);
        page.addView(wakeButton, buttonParams);
        wakeButton.setOnClickListener(v -> sendPacket());

        statusView = text("Готово к отправке", 14, MUTED, false);
        statusView.setGravity(Gravity.CENTER);
        LinearLayout.LayoutParams statusParams = params(-1, -2);
        statusParams.topMargin = dp(15);
        page.addView(statusView, statusParams);

        TextView hint = text("Дома укажите broadcast-адрес сети. Вне дома подключитесь к домашнему VPN или настройте на роутере Wake-on-LAN и укажите его внешний IP/DDNS. Роутер должен переслать пакет в локальную сеть.", 13, MUTED, false);
        hint.setLineSpacing(dp(3), 1f);
        hint.setPadding(dp(2), dp(18), dp(2), dp(4));
        page.addView(hint);
        setContentView(scroll);
    }

    private void addLabel(LinearLayout parent, String value) { addLabel(parent, value, 0); }
    private void addLabel(LinearLayout parent, String value, int top) {
        TextView label = text(value, 11, MUTED, true);
        label.setLetterSpacing(0.08f);
        LinearLayout.LayoutParams lp = params(-1, -2);
        lp.topMargin = dp(top);
        parent.addView(label, lp);
    }

    private EditText input(String hint, int type) {
        EditText e = new EditText(this);
        e.setSingleLine(true);
        e.setTextSize(15);
        e.setTextColor(TEXT);
        e.setHintTextColor(Color.rgb(111, 129, 141));
        e.setHint(hint);
        e.setInputType(type);
        e.setPadding(dp(14), 0, dp(14), 0);
        e.setBackground(round(Color.rgb(16, 24, 32), dp(12)));
        return e;
    }

    private TextView text(String value, int size, int color, boolean bold) {
        TextView t = new TextView(this);
        t.setText(value);
        t.setTextSize(size);
        t.setTextColor(color);
        if (bold) t.setTypeface(Typeface.DEFAULT, Typeface.BOLD);
        return t;
    }

    private GradientDrawable round(int color, int radius) {
        GradientDrawable d = new GradientDrawable();
        d.setColor(color);
        d.setCornerRadius(radius);
        return d;
    }

    private LinearLayout.LayoutParams params(int width, int height) {
        return new LinearLayout.LayoutParams(width, height);
    }

    private int dp(float value) { return (int) (value * getResources().getDisplayMetrics().density + 0.5f); }

    private void loadSaved() {
        android.content.SharedPreferences p = getSharedPreferences("wake", MODE_PRIVATE);
        macInput.setText(p.getString("mac", ""));
        broadcastInput.setText(p.getString("broadcast", "255.255.255.255"));
        portInput.setText(p.getString("port", "9"));
    }

    private void sendPacket() {
        String mac = macInput.getText().toString().trim().replace(":", "").replace("-", "").replace(" ", "");
        String broadcast = broadcastInput.getText().toString().trim();
        String portText = portInput.getText().toString().trim();
        if (!mac.matches("(?i)[0-9a-f]{12}")) {
            macInput.setError("Введите MAC из 12 шестнадцатеричных символов");
            return;
        }
        boolean ipv4 = broadcast.matches("(?:[0-9]{1,3}\\.){3}[0-9]{1,3}");
        boolean hostname = broadcast.length() <= 253
                && broadcast.matches("(?i)(?=.{1,253}$)(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)(?:\\.(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?))*\\.?");
        if (!ipv4 && !hostname) {
            broadcastInput.setError("Введите IPv4-адрес или DDNS-имя");
            return;
        }
        if (ipv4) {
            for (String octet : broadcast.split("\\.")) {
                try {
                    int value = Integer.parseInt(octet);
                    if (value > 255) throw new NumberFormatException();
                } catch (NumberFormatException ex) {
                    broadcastInput.setError("Каждая часть IPv4-адреса должна быть от 0 до 255");
                    return;
                }
            }
        }
        int port;
        try {
            port = Integer.parseInt(portText);
            if (port < 1 || port > 65535) throw new NumberFormatException();
        } catch (NumberFormatException ex) {
            portInput.setError("Порт должен быть от 1 до 65535");
            return;
        }
        final String savedMac = mac;
        final String savedBroadcast = broadcast;
        final int savedPort = port;
        getSharedPreferences("wake", MODE_PRIVATE).edit()
                .putString("mac", macInput.getText().toString().trim())
                .putString("broadcast", broadcast)
                .putString("port", portText).apply();
        ((InputMethodManager) getSystemService(Context.INPUT_METHOD_SERVICE)).hideSoftInputFromWindow(wakeButton.getWindowToken(), 0);
        setBusy(true, "Отправляю пакет…");
        worker.execute(() -> {
            String result;
            try {
                byte[] packet = makePacket(savedMac);
                InetAddress address = null;
                for (InetAddress resolved : InetAddress.getAllByName(savedBroadcast)) {
                    if (resolved instanceof java.net.Inet4Address) {
                        address = resolved;
                        break;
                    }
                }
                if (address == null) throw new IllegalArgumentException("Имя не разрешилось в IPv4-адрес");
                DatagramSocket socket = new DatagramSocket();
                try {
                    socket.setBroadcast(true);
                    socket.send(new DatagramPacket(packet, packet.length, address, savedPort));
                } finally {
                    socket.close();
                }
                result = "Magic packet отправлен";
            } catch (Exception ex) {
                result = "Не удалось отправить: " + (ex.getMessage() == null ? "проверьте подключение" : ex.getMessage());
            }
            final String message = result;
            runOnUiThread(() -> setBusy(false, message));
        });
    }

    private byte[] makePacket(String macText) {
        byte[] mac = new byte[6];
        for (int i = 0; i < 6; i++) mac[i] = (byte) Integer.parseInt(macText.substring(i * 2, i * 2 + 2), 16);
        byte[] packet = new byte[6 + 16 * 6];
        for (int i = 0; i < 6; i++) packet[i] = (byte) 0xff;
        for (int repeat = 0; repeat < 16; repeat++) System.arraycopy(mac, 0, packet, 6 + repeat * 6, 6);
        return packet;
    }

    private void setBusy(boolean busy, String message) {
        wakeButton.setEnabled(!busy);
        wakeButton.setAlpha(busy ? 0.6f : 1f);
        statusView.setText(message);
        statusView.setTextColor(message.startsWith("Не удалось") ? Color.rgb(255, 133, 133) : (busy ? ACCENT : MUTED));
    }

    @Override
    protected void onDestroy() {
        worker.shutdownNow();
        super.onDestroy();
    }
}
