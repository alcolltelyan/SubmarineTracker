package main

import (
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync"
	"testing"
	"time"
)

// Each test owns its fake endpoint; no Discord or database connection is used.
func TestSendWebhookResponses(t *testing.T) {
	for _, tc := range []struct {
		name     string
		statuses []int
		body     string
		attempts int
	}{
		{"ok", []int{200}, "", 1},
		{"no-content", []int{204}, "", 1},
		{"retry-success", []int{429, 204}, `{"retry_after":0.001}`, 2},
		{"retry-exhaustion", []int{429, 429, 429, 429}, `{"retry_after":0.001}`, 4},
		{"terminal-error", []int{400}, "bad request", 1},
		{"invalid-rate-limit", []int{429}, "not json", 1},
	} {
		t.Run(tc.name, func(t *testing.T) {
			var bodies []string
			var sent []time.Time
			server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				body, err := io.ReadAll(r.Body)
				if err != nil {
					t.Error(err)
				}
				bodies = append(bodies, string(body))
				sent = append(sent, time.Now())
				status := tc.statuses[len(bodies)-1]
				w.WriteHeader(status)
				if status != http.StatusNoContent {
					_, _ = io.WriteString(w, tc.body)
				}
			}))
			defer server.Close()
			SendWebhook(server.URL, NewContent())
			if len(bodies) != tc.attempts {
				t.Fatalf("got %d attempts, want %d", len(bodies), tc.attempts)
			}
			for i, body := range bodies {
				if body != bodies[0] || !strings.Contains(body, "Submarine Tracker") {
					t.Fatal("payload was lost or changed during retry")
				}
				if i > 0 && sent[i].Sub(sent[i-1]) < 240*time.Millisecond {
					t.Fatal("retry_after safety delay was not honored")
				}
			}
		})
	}
}

func TestThrottleReservations(t *testing.T) {
	const same = "test://same-webhook"
	throttleMu.Lock()
	nextSendAtByWebhook = map[string]time.Time{}
	throttleMu.Unlock()
	var mu sync.Mutex
	var times []time.Time
	var workers sync.WaitGroup
	for range 2 {
		workers.Add(1)
		go func() {
			defer workers.Done()
			waitForReservedSendSlot(same)
			mu.Lock()
			times = append(times, time.Now())
			mu.Unlock()
		}()
	}
	workers.Wait()
	if times[1].Sub(times[0]) < 480*time.Millisecond {
		t.Fatal("same-webhook reservations were not spaced by 500ms")
	}
	setNextSendAt(same, time.Now().Add(2*time.Second))
	started := time.Now()
	waitForReservedSendSlot("test://independent-webhook")
	if time.Since(started) >= 250*time.Millisecond {
		t.Fatal("one destination blocked an unrelated destination")
	}
}

func TestSendWebhookTransportFailure(t *testing.T) {
	// Close the local listener before sending: exercise the existing transport-error path.
	server := httptest.NewServer(http.HandlerFunc(func(http.ResponseWriter, *http.Request) {}))
	server.Close()
	SendWebhook(server.URL, NewContent())
}
