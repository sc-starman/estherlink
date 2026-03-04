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

        checkoutResult.innerHTML = `Order <span class="font-mono">${payload.orderId}</span> created. Intent <span class="font-mono">${payload.intentId}</span>. Status: ${payload.status}.`;
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