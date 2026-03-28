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
  const couponInput = document.getElementById('coupon-code-input');
  const couponApplyButton = document.getElementById('coupon-apply');
  const couponClearButton = document.getElementById('coupon-clear');
  const couponQuoteMessage = document.getElementById('coupon-quote-message');
  const summaryBase = document.getElementById('summary-base');
  const summaryDiscount = document.getElementById('summary-discount');
  const summaryFinal = document.getElementById('summary-final');
  const summaryCoupon = document.getElementById('summary-coupon');
  const redirectModal = document.getElementById('payment-redirect-modal');
  const redirectCountdown = document.getElementById('payment-redirect-countdown');
  const redirectAmount = document.getElementById('payment-redirect-amount');
  const redirectCoupon = document.getElementById('payment-redirect-coupon');
  const redirectGoNowButton = document.getElementById('payment-redirect-go-now');
  const redirectCloseButton = document.getElementById('payment-redirect-close');
  let redirectTimerId = null;
  let redirectIntervalId = null;
  let redirectTargetUrl = '';
  let remainingSeconds = 10;
  let appliedCouponCode = null;
  let appliedDiscountPercent = null;

  const formatMoney = (value) => {
    const parsed = Number(value);
    if (Number.isFinite(parsed)) {
      return parsed.toFixed(2);
    }
    return String(value ?? '0.00');
  };

  const setQuoteMessage = (message, isError) => {
    if (!couponQuoteMessage) {
      return;
    }

    couponQuoteMessage.textContent = message || '';
    couponQuoteMessage.classList.remove('text-red-300', 'text-emerald-300', 'text-slate-400');
    couponQuoteMessage.classList.add(isError ? 'text-red-300' : 'text-emerald-300');
  };

  const applyQuoteToUi = (payload) => {
    if (!payload) {
      return;
    }

    const baseAmount = formatMoney(payload.baseAmount);
    const discountAmount = formatMoney(payload.discountAmount);
    const finalAmount = formatMoney(payload.finalAmount);
    const couponCode = payload.appliedCouponCode || null;
    const discountPercent = payload.appliedDiscountPercent ?? null;

    appliedCouponCode = couponCode;
    appliedDiscountPercent = discountPercent;

    if (summaryBase) summaryBase.textContent = `$${baseAmount}`;
    if (summaryDiscount) summaryDiscount.textContent = `-$${discountAmount}`;
    if (summaryFinal) summaryFinal.textContent = `$${finalAmount}`;
    if (summaryCoupon) {
      summaryCoupon.textContent = couponCode
        ? `${couponCode}${discountPercent !== null ? ` (${discountPercent}%)` : ''}`
        : 'none';
    }
  };

  const getCouponInputValue = () => {
    if (!couponInput) {
      return null;
    }

    const trimmed = couponInput.value.trim();
    return trimmed.length > 0 ? trimmed : null;
  };

  const quoteCheckout = async (couponCode, quiet) => {
    try {
      const response = await fetch('/app/api/checkout/quote', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ couponCode: couponCode || null })
      });

      const payload = await response.json();
      if (!response.ok) {
        if (!quiet) {
          setQuoteMessage(payload.message || 'Coupon is invalid.', true);
        }
        return { ok: false, payload };
      }

      applyQuoteToUi(payload);
      if (!quiet) {
        if (payload.appliedCouponCode) {
          setQuoteMessage(`Coupon applied: ${payload.appliedCouponCode} (${payload.appliedDiscountPercent ?? 0}% off).`, false);
        } else {
          setQuoteMessage('No coupon applied.', false);
        }
      }

      return { ok: true, payload };
    } catch {
      if (!quiet) {
        setQuoteMessage('Unable to validate coupon right now.', true);
      }
      return { ok: false, payload: null };
    }
  };

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

  if (couponApplyButton) {
    couponApplyButton.addEventListener('click', async () => {
      couponApplyButton.disabled = true;
      const couponCode = getCouponInputValue();
      await quoteCheckout(couponCode, false);
      couponApplyButton.disabled = false;
    });
  }

  if (couponClearButton) {
    couponClearButton.addEventListener('click', async () => {
      if (couponInput) {
        couponInput.value = '';
      }
      couponClearButton.disabled = true;
      await quoteCheckout(null, false);
      couponClearButton.disabled = false;
    });
  }

  if (createIntentButton && checkoutResult) {
    createIntentButton.addEventListener('click', async () => {
      createIntentButton.disabled = true;
      checkoutResult.textContent = 'Creating payment intent...';
      try {
        const couponCode = appliedCouponCode || getCouponInputValue();
        const response = await fetch('/app/api/checkout/create-intent', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ couponCode: couponCode || null })
        });
        const payload = await response.json();
        if (!response.ok) {
          checkoutResult.textContent = payload.message || 'Create intent failed.';
          return;
        }

        applyQuoteToUi(payload);

        const intentId = payload.intentId;
        if (!intentId) {
          checkoutResult.textContent = 'Create intent succeeded but intent id is missing.';
          return;
        }

        redirectTargetUrl = `https://gate.paykrypt.io/pay/${encodeURIComponent(intentId)}`;
        remainingSeconds = 10;

        if (redirectAmount) {
          redirectAmount.textContent = `$${formatMoney(payload.finalAmount ?? payload.amount ?? 0)}`;
        }

        if (redirectCoupon) {
          redirectCoupon.textContent = payload.appliedCouponCode
            ? `${payload.appliedCouponCode}${payload.appliedDiscountPercent !== null && payload.appliedDiscountPercent !== undefined ? ` (${payload.appliedDiscountPercent}%)` : ''}`
            : 'none';
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
