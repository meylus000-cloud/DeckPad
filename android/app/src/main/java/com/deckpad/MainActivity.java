package com.deckpad;

import android.app.Activity;
import android.graphics.Color;
import android.graphics.drawable.GradientDrawable;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.view.Gravity;
import android.view.MotionEvent;
import android.view.View;
import android.view.ViewGroup;
import android.view.WindowManager;
import android.widget.LinearLayout;
import android.widget.TextView;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.BufferedReader;
import java.io.BufferedWriter;
import java.io.IOException;
import java.io.InputStreamReader;
import java.io.OutputStreamWriter;
import java.io.PrintWriter;
import java.net.InetSocketAddress;
import java.net.ServerSocket;
import java.net.Socket;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/**
 * DeckPad: touch "Stream Deck". The PC connects to this app over USB (adb forward)
 * or USB tethering and pushes the button layout; taps are sent back to the PC.
 * Protocol: one JSON object per line, UTF-8.
 */
public class MainActivity extends Activity {
    static final int PORT = 38200;

    private final Handler ui = new Handler(Looper.getMainLooper());
    private final ExecutorService sender = Executors.newSingleThreadExecutor();
    private LinearLayout grid;
    private TextView status;
    private float dp;

    private volatile ServerSocket server;
    private volatile Socket current;
    private volatile PrintWriter writer;
    private volatile boolean running = true;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        dp = getResources().getDisplayMetrics().density;
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);

        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setBackgroundColor(0xFF0E0F13);

        status = new TextView(this);
        status.setTextSize(12);
        status.setGravity(Gravity.CENTER_VERTICAL);
        status.setPadding(px(12), 0, px(12), 0);
        status.setBackgroundColor(0xFF16181F);
        root.addView(status, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, px(26)));

        grid = new LinearLayout(this);
        grid.setOrientation(LinearLayout.VERTICAL);
        grid.setPadding(px(6), px(6), px(6), px(6));
        root.addView(grid, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, 0, 1f));

        setContentView(root);

        String saved = getPreferences(MODE_PRIVATE).getString("config", null);
        if (saved != null) render(saved); else showHint();
        setStatus(false);
        startServer();
    }

    @Override
    public void onWindowFocusChanged(boolean hasFocus) {
        super.onWindowFocusChanged(hasFocus);
        if (hasFocus) {
            getWindow().getDecorView().setSystemUiVisibility(
                    View.SYSTEM_UI_FLAG_HIDE_NAVIGATION | View.SYSTEM_UI_FLAG_LOW_PROFILE);
        }
    }

    @Override
    protected void onDestroy() {
        super.onDestroy();
        running = false;
        closeCurrent();
        try { if (server != null) server.close(); } catch (IOException ignored) { }
        sender.shutdownNow();
    }

    private int px(int v) { return (int) (v * dp + 0.5f); }

    // ---------------------------------------------------------------- UI

    private void setStatus(boolean connected) {
        if (connected) {
            status.setText("● Connected to PC");
            status.setTextColor(0xFF4ADE80);
        } else {
            status.setText("○ Waiting for PC (USB cable, DeckPad on Windows)");
            status.setTextColor(0xFF8B90A0);
        }
    }

    private void showHint() {
        grid.removeAllViews();
        TextView t = new TextView(this);
        t.setText("Open DeckPad on your PC\nand connect the USB cable");
        t.setTextColor(0xFF8B90A0);
        t.setTextSize(20);
        t.setGravity(Gravity.CENTER);
        grid.addView(t, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
    }

    private void render(String json) {
        try {
            JSONObject cfg = new JSONObject(json);
            int cols = Math.max(1, Math.min(12, cfg.optInt("cols", 5)));
            int rows = Math.max(1, Math.min(8, cfg.optInt("rows", 3)));
            JSONObject[] cells = new JSONObject[cols * rows];
            JSONArray arr = cfg.optJSONArray("buttons");
            if (arr != null) {
                for (int k = 0; k < arr.length(); k++) {
                    JSONObject b = arr.getJSONObject(k);
                    int i = b.optInt("i", -1);
                    if (i >= 0 && i < cells.length) cells[i] = b;
                }
            }
            grid.removeAllViews();
            for (int r = 0; r < rows; r++) {
                LinearLayout row = new LinearLayout(this);
                row.setOrientation(LinearLayout.HORIZONTAL);
                grid.addView(row, new LinearLayout.LayoutParams(
                        ViewGroup.LayoutParams.MATCH_PARENT, 0, 1f));
                for (int c = 0; c < cols; c++) {
                    int idx = r * cols + c;
                    TextView v = makeButton(idx, cells[idx]);
                    LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(
                            0, ViewGroup.LayoutParams.MATCH_PARENT, 1f);
                    lp.setMargins(px(5), px(5), px(5), px(5));
                    row.addView(v, lp);
                }
            }
        } catch (Exception e) {
            showHint();
        }
    }

    private TextView makeButton(final int index, JSONObject b) {
        final TextView v = new TextView(this);
        v.setGravity(Gravity.CENTER);
        v.setMaxLines(3);
        v.setPadding(px(6), px(6), px(6), px(6));
        GradientDrawable bg = new GradientDrawable();
        bg.setCornerRadius(px(14));
        if (b == null) {
            bg.setColor(0xFF15171D);
            v.setBackgroundDrawable(bg);
            return v;
        }
        int color = 0xFF2D3748;
        try { color = Color.parseColor(b.optString("color", "#2D3748")); } catch (Exception ignored) { }
        bg.setColor(color);
        v.setBackgroundDrawable(bg);
        v.setText(b.optString("label", ""));
        v.setTextSize(22);
        v.setTextColor(luminance(color) > 150 ? 0xFF101114 : 0xFFFFFFFF);
        v.setOnTouchListener(new View.OnTouchListener() {
            @Override
            public boolean onTouch(View view, MotionEvent e) {
                int a = e.getAction();
                if (a == MotionEvent.ACTION_DOWN) {
                    view.setAlpha(0.55f);
                    view.performHapticFeedback(android.view.HapticFeedbackConstants.VIRTUAL_KEY);
                    send("{\"type\":\"press\",\"i\":" + index + "}");
                } else if (a == MotionEvent.ACTION_UP || a == MotionEvent.ACTION_CANCEL) {
                    view.setAlpha(1f);
                }
                return true;
            }
        });
        return v;
    }

    private static int luminance(int c) {
        return (int) (0.299 * Color.red(c) + 0.587 * Color.green(c) + 0.114 * Color.blue(c));
    }

    // ----------------------------------------------------------- network

    private void send(final String line) {
        sender.execute(new Runnable() {
            @Override
            public void run() {
                PrintWriter w = writer;
                if (w != null) w.println(line);
            }
        });
    }

    private void closeCurrent() {
        Socket s = current;
        current = null;
        writer = null;
        if (s != null) {
            try { s.close(); } catch (IOException ignored) { }
        }
    }

    private void startServer() {
        new Thread(new Runnable() {
            @Override
            public void run() {
                while (running) {
                    try {
                        ServerSocket ss = new ServerSocket();
                        ss.setReuseAddress(true);
                        ss.bind(new InetSocketAddress(PORT));
                        server = ss;
                        while (running) {
                            final Socket s = ss.accept();
                            s.setTcpNoDelay(true);
                            s.setSoTimeout(7000);
                            closeCurrent();
                            current = s;
                            new Thread(new Runnable() {
                                @Override
                                public void run() { serve(s); }
                            }).start();
                        }
                    } catch (IOException e) {
                        try { Thread.sleep(1500); } catch (InterruptedException ignored) { }
                    }
                }
            }
        }).start();
    }

    private void serve(final Socket s) {
        try {
            BufferedReader in = new BufferedReader(new InputStreamReader(s.getInputStream(), "UTF-8"));
            PrintWriter w = new PrintWriter(new BufferedWriter(
                    new OutputStreamWriter(s.getOutputStream(), "UTF-8")), true);
            w.println("{\"type\":\"hello\"}");
            writer = w;
            ui.post(new Runnable() { public void run() { setStatus(true); } });
            String line;
            while ((line = in.readLine()) != null) {
                handleLine(line);
            }
        } catch (IOException ignored) {
            // disconnect or timeout
        } finally {
            try { s.close(); } catch (IOException ignored) { }
            if (current == s) {
                current = null;
                writer = null;
                ui.post(new Runnable() { public void run() { setStatus(false); } });
            }
        }
    }

    private void handleLine(final String line) {
        try {
            JSONObject o = new JSONObject(line);
            String type = o.optString("type");
            if ("ping".equals(type)) {
                send("{\"type\":\"pong\"}");
            } else if ("config".equals(type)) {
                getPreferences(MODE_PRIVATE).edit().putString("config", line).commit();
                ui.post(new Runnable() { public void run() { render(line); } });
            }
        } catch (Exception ignored) { }
    }
}
