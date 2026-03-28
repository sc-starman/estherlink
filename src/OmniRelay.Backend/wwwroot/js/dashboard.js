(() => {
  const trialButton = document.querySelector('[data-action="start-trial"]');
  const trialResult = document.getElementById('trial-result');
  if (trialButton && trialResult) {
    trialButton.addEventListener('click', async () => {
      trialButton.disabled = true;
      trialResult.textContent = 'Requesting trial...';
      try {
        const response = await fetch('/app/api/trial/request', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' }
        });
        const payload = await response.json();
        if (!response.ok) {
          trialResult.textContent = payload.message || 'Trial request failed.';
        } else {
          trialResult.textContent = payload.licenseKey
            ? `Trial started. License: ${payload.licenseKey} (expires ${payload.expiresAt}).`
            : payload.message;
        }
      } catch {
        trialResult.textContent = 'Failed to contact server.';
      } finally {
        trialButton.disabled = false;
      }
    });
  }

  const createIntentButton = document.querySelector('[data-action="create-intent"]');
  const checkoutResult = document.getElementById('checkout-result');
  const redirectModal = document.getElementById('payment-redirect-modal');
  const redirectCountdown = document.getElementById('payment-redirect-countdown');
  const redirectAmount = document.getElementById('payment-redirect-amount');
  const redirectGoNowButton = document.getElementById('payment-redirect-go-now');
  const redirectCloseButton = document.getElementById('payment-redirect-close');
  let redirectTimerId = null;
  let redirectIntervalId = null;
  let redirectTargetUrl = '';
  let remainingSeconds = 10;

  const clearRedirectTimers = () => {
    if (redirectTimerId) {
      clearTimeout(redirectTimerId);
      redirectTimerId = null;
    }
    if (redirectIntervalId) {
      clearInterval(redirectIntervalId);
      redirectIntervalId = null;
    }
  };

  const closeRedirectModal = () => {
    clearRedirectTimers();
    redirectTargetUrl = '';
    if (redirectModal) {
      redirectModal.classList.add('hidden');
    }
  };

  const redirectNow = () => {
    if (!redirectTargetUrl) {
      return;
    }
    const url = redirectTargetUrl;
    closeRedirectModal();
    window.location.href = url;
  };

  if (redirectCloseButton) {
    redirectCloseButton.addEventListener('click', () => {
      closeRedirectModal();
    });
  }

  if (redirectGoNowButton) {
    redirectGoNowButton.addEventListener('click', () => {
      redirectNow();
    });
  }

  if (redirectModal) {
    redirectModal.addEventListener('click', (event) => {
      if (event.target === redirectModal) {
        closeRedirectModal();
      }
    });
  }

  document.addEventListener('keydown', (event) => {
    if (event.key === 'Escape' && redirectModal && !redirectModal.classList.contains('hidden')) {
      closeRedirectModal();
    }
  });

  if (createIntentButton && checkoutResult) {
    createIntentButton.addEventListener('click', async () => {
      createIntentButton.disabled = true;
      checkoutResult.textContent = 'Creating payment intent...';
      try {
        const response = await fetch('/app/api/checkout/create-intent', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' }
        });
        const payload = await response.json();
        if (!response.ok) {
          checkoutResult.textContent = payload.message || 'Create intent failed.';
          return;
        }

        const intentId = payload.intentId;
        if (!intentId) {
          checkoutResult.textContent = 'Create intent succeeded but intent id is missing.';
          return;
        }

        const amount =
          payload.amount ??
          payload.fiatAmount ??
          createIntentButton.getAttribute('data-payment-amount') ??
          '0.00';

        redirectTargetUrl = `https://gate.paykrypt.io/pay/${encodeURIComponent(intentId)}`;
        remainingSeconds = 10;

        if (redirectAmount) {
          const parsedAmount = Number(amount);
          const amountText = Number.isFinite(parsedAmount) ? parsedAmount.toFixed(2) : String(amount);
          redirectAmount.textContent = `$${amountText}`;
        }

        if (redirectCountdown) {
          redirectCountdown.textContent = String(remainingSeconds);
        }

        checkoutResult.textContent = '';

        if (redirectModal) {
          redirectModal.classList.remove('hidden');
        }

        clearRedirectTimers();
        redirectIntervalId = setInterval(() => {
          remainingSeconds = Math.max(remainingSeconds - 1, 0);
          if (redirectCountdown) {
            redirectCountdown.textContent = String(remainingSeconds);
          }
        }, 1000);

        redirectTimerId = setTimeout(() => {
          redirectNow();
        }, 10000);
      } catch {
        checkoutResult.textContent = 'Failed to contact server.';
      } finally {
        createIntentButton.disabled = false;
      }
    });
  }

  document.querySelectorAll('.poll-order').forEach((button) => {
    button.addEventListener('click', async () => {
      const orderId = button.getAttribute('data-order-id');
      if (!orderId) {
        return;
      }

      const target = document.getElementById(`order-${orderId}`);
      if (!target) {
        return;
      }

      button.disabled = true;
      target.textContent = 'Refreshing order...';
      try {
        const response = await fetch(`/app/api/checkout/${orderId}/status?refresh=true`);
        if (!response.ok) {
          target.textContent = 'Unable to load order status.';
          return;
        }

        const payload = await response.json();
        target.textContent = payload.licenseKey
          ? `Paid. License issued: ${payload.licenseKey}`
          : `Order status: ${payload.orderStatus}, intent: ${payload.intentStatus}`;
      } catch {
        target.textContent = 'Refresh failed.';
      } finally {
        button.disabled = false;
      }
    });
  });

  document.querySelectorAll('.copy-license').forEach((button) => {
    button.addEventListener('click', async () => {
      const value = button.getAttribute('data-key') || '';
      if (!value) {
        return;
      }

      await navigator.clipboard.writeText(value);
      button.textContent = 'Copied';
      setTimeout(() => {
        button.textContent = 'Copy';
      }, 1200);
    });
  });
})();
