package main

import (
	"bytes"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"os"
	"sync"
	"time"
)

const maxRateLimitRetries = 3
const minimumSendInterval = 500 * time.Millisecond

var throttleMu sync.Mutex
var nextSendAtByWebhook = map[string]time.Time{}

// ActiveReturn - Database layout of a running timer that is soon to be executed.
type ActiveReturn struct {
	Id          int64
	WebhookURL  string
	Content     string
	Name        string
	Mention     int64
	RoleMention int64
	ReturnTime  int64
}

type RateLimitResponse struct {
	Message    string  `json:"message"`
	RetryAfter float64 `json:"retry_after"`
	Global     bool    `json:"global"`
	ErrorCode  int     `json:"code,omitempty"`
}

// HandleReturn - Sleeps until the time has elapsed and prepares the notification.
func HandleReturn(active ActiveReturn) {
	var sleepTime = active.ReturnTime - time.Now().Unix()
	time.Sleep(time.Duration(sleepTime) * time.Second)

	var content = NewContent()

	var mentionContent = ""
	if active.Mention > 0 {
		mentionContent += fmt.Sprintf(`<@%d>`, active.Mention)
	}

	if active.RoleMention > 0 {
		mentionContent += fmt.Sprintf(`<@&%d>`, active.RoleMention)
	}

	if len(mentionContent) > 0 {
		content.Content = mentionContent
	}

	var embed = Embed{}
	embed.Title = active.Name
	embed.Description = active.Content
	embed.Color = "8447519"

	content.Embeds = []Embed{embed}

	SendWebhook(active.WebhookURL, content)
}

// SendWebhook - Sends the webhook as JSON encoded payload and checks the return value for errors.
func SendWebhook(webhookUrl string, content WebhookContent) {
	payload, err := json.Marshal(content)
	if err != nil {
		fmt.Fprintf(os.Stderr, "Unable to encode as JSON: %v\n", err)
		return
	}

	for attempt := 0; attempt <= maxRateLimitRetries; attempt++ {
		waitForReservedSendSlot(webhookUrl)

		resp, err := http.Post(webhookUrl, "application/json", bytes.NewReader(payload))
		if err != nil {
			fmt.Fprintf(os.Stderr, "Unable to send webhook: %v\n", err)
			return
		}

		if resp.StatusCode == http.StatusTooManyRequests {
			var rateLimit RateLimitResponse
			err = json.NewDecoder(resp.Body).Decode(&rateLimit)
			resp.Body.Close()
			if err != nil {
				fmt.Fprintf(os.Stderr, "Unable to convert response to rate-limit json: %v\n", err)
				return
			}

			if attempt >= maxRateLimitRetries {
				fmt.Fprintf(os.Stderr, "Webhook rate limited after %d retries: %s\n", maxRateLimitRetries, rateLimit.Message)
				return
			}

			delay := time.Duration(rateLimit.RetryAfter*float64(time.Second)) + 250*time.Millisecond
			setNextSendAt(webhookUrl, time.Now().Add(delay))
			fmt.Printf("Webhook rate limited. Retrying after %s (attempt %d/%d)\n", delay, attempt+1, maxRateLimitRetries)
			continue
		}

		if resp.StatusCode != http.StatusOK && resp.StatusCode != http.StatusNoContent {
			responseBody, readErr := io.ReadAll(resp.Body)
			resp.Body.Close()
			if readErr != nil {
				fmt.Fprintf(os.Stderr, "Unable to read response body: %v\n", readErr)
				return
			}

			fmt.Fprintf(os.Stderr, "Error response was: %s\n", string(responseBody))
			setNextSendAt(webhookUrl, time.Now().Add(minimumSendInterval))
			return
		}

		resp.Body.Close()
		setNextSendAt(webhookUrl, time.Now().Add(minimumSendInterval))
		return
	}
}

func waitForReservedSendSlot(webhookUrl string) {
	throttleMu.Lock()
	scheduledAt := nextSendAtByWebhook[webhookUrl]
	now := time.Now()
	if scheduledAt.Before(now) {
		scheduledAt = now
	}
	nextSendAtByWebhook[webhookUrl] = scheduledAt.Add(minimumSendInterval)
	throttleMu.Unlock()

	if delay := time.Until(scheduledAt); delay > 0 {
		time.Sleep(delay)
	}
}

func setNextSendAt(webhookUrl string, nextSendAt time.Time) {
	throttleMu.Lock()
	nextSendAtByWebhook[webhookUrl] = nextSendAt
	throttleMu.Unlock()
}
