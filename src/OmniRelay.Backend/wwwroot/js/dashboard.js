(() => {
  const i18n = window.omniRelayI18n || {};
  const t = (key, fallback) => (Object.prototype.hasOwnProperty.call(i18n, key) ? i18n[key] : fallback);
  const formatTemplate = (template, values) => {
    let output = template || '';
    Object.entries(values || {}).forEach(([name, value]) => {
      output = output.replaceAll(`{${name}}`, value ?? '');
    });
    return output;
  };

  const trialButton = document.querySelector('[data-action="start-trial"]');
  const trialResult = document.getElementById('trial-result');
  if (trialButton && trialResult) {
    trialButton.addEventListener('click', async () => {
      trialButton.disabled = true;
      trialResult.textContent = t('trial.requesting', 'Requesting trial...');
      try {
        const response = await fetch('/app/api/trial/request', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' }
        });
        const payload = await response.json();
        if (!response.ok) {
          trialResult.textContent = payload.message || t('trial.requestFailed', 'Trial request failed.');
        } else {
          trialResult.textContent = payload.licenseKey
            ? formatTemplate(
                t('trial.started', 'Trial started. License: {licenseKey} (expires {expiresAt}).'),
                { licenseKey: payload.licenseKey, expiresAt: payload.expiresAt || '-' }
              )
            : payload.message;
        }
      } catch {
        trialResult.textContent = t('server.failed', 'Failed to contact server.');
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
  const waitingModal = document.getElementById('payment-waiting-modal');
  const waitingStatus = document.getElementById('payment-waiting-status');
  const waitingAmount = document.getElementById('payment-waiting-amount');
  const waitingCoupon = document.getElementById('payment-waiting-coupon');
  const waitingCheckButton = document.getElementById('payment-waiting-check');
  const waitingCloseButton = document.getElementById('payment-waiting-close');
  const waitingCloseIconButton = document.getElementById('payment-waiting-close-icon');
  let redirectTimerId = null;
  let redirectIntervalId = null;
  let redirectTargetUrl = '';
  let remainingSeconds = 10;
  let appliedCouponCode = null;
  let appliedDiscountPercent = null;
  let pendingOrderId = '';
  let pendingStatusPolling = false;

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
        : t('checkout.summary.none', 'none');
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
          setQuoteMessage(payload.message || t('coupon.invalid', 'Coupon is invalid.'), true);
        }
        return { ok: false, payload };
      }

      applyQuoteToUi(payload);
      if (!quiet) {
        if (payload.appliedCouponCode) {
          setQuoteMessage(
            formatTemplate(
              t('coupon.applied', 'Coupon applied: {code} ({percent}% off).'),
              {
                code: payload.appliedCouponCode,
                percent: payload.appliedDiscountPercent ?? 0
              }
            ),
            false
          );
        } else {
          setQuoteMessage(t('coupon.none', 'No coupon applied.'), false);
        }
      }

      return { ok: true, payload };
    } catch {
      if (!quiet) {
        setQuoteMessage(t('coupon.unavailable', 'Unable to validate coupon right now.'), true);
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

  const closeWaitingModal = () => {
    if (waitingModal) {
      waitingModal.classList.add('hidden');
    }
    pendingOrderId = '';
  };

  const setWaitingStatusText = (text, level) => {
    if (!waitingStatus) {
      return;
    }

    waitingStatus.textContent = text || '';
    waitingStatus.classList.remove('text-cyan-300', 'text-emerald-300', 'text-red-300');
    waitingStatus.classList.add(
      level === 'success'
        ? 'text-emerald-300'
        : level === 'error'
          ? 'text-red-300'
          : 'text-cyan-300'
    );
  };

  const updateInlineOrderMessage = (orderId, message) => {
    if (!orderId || !message) {
      return;
    }

    const target = document.getElementById(`order-${orderId}`);
    if (target) {
      target.textContent = message;
    }
  };

  const refreshPendingOrderStatus = async (quiet) => {
    if (!pendingOrderId || pendingStatusPolling) {
      return;
    }

    pendingStatusPolling = true;
    setWaitingStatusText(t('checkout.waitingChecking', 'Checking payment status...'), 'pending');

    try {
      const response = await fetch(`/app/api/checkout/${pendingOrderId}/status?refresh=true`);
      if (!response.ok) {
        if (!quiet) {
          setWaitingStatusText(t('checkout.statusCheckFailed', 'Unable to check payment status right now.'), 'error');
        }
        return;
      }

      const payload = await response.json();
      if (payload.isPaid) {
        const paidText = payload.licenseKey
          ? formatTemplate(
              t('order.paidIssued', 'Paid. License issued: {licenseKey}'),
              { licenseKey: payload.licenseKey }
            )
          : t('checkout.paymentCompleted', 'Payment confirmed.');

        setWaitingStatusText(paidText, 'success');
        updateInlineOrderMessage(pendingOrderId, paidText);
        if (checkoutResult) {
          checkoutResult.textContent = paidText;
        }
        pendingOrderId = '';
        return;
      }

      const pendingText = formatTemplate(
        t('checkout.paymentStillPending', 'Payment still pending ({orderStatus} / {intentStatus}).'),
        {
          orderStatus: payload.orderStatus ?? '-',
          intentStatus: payload.intentStatus ?? '-'
        }
      );

      setWaitingStatusText(pendingText, 'pending');
      updateInlineOrderMessage(pendingOrderId, pendingText);
      if (checkoutResult) {
        checkoutResult.textContent = pendingText;
      }
    } catch {
      if (!quiet) {
        setWaitingStatusText(t('checkout.statusCheckFailed', 'Unable to check payment status right now.'), 'error');
      }
    } finally {
      pendingStatusPolling = false;
    }
  };

  const redirectNow = () => {
    if (!redirectTargetUrl) {
      return;
    }

    const url = redirectTargetUrl;
    const popup = window.open(url, '_blank', 'noopener,noreferrer');
    if (!popup) {
      if (checkoutResult) {
        checkoutResult.textContent = t('checkout.popupBlocked', 'Unable to open payment tab. Please allow pop-ups and click Go Now again.');
      }
      return;
    }

    closeRedirectModal();

    if (waitingModal) {
      waitingModal.classList.remove('hidden');
    }

    setWaitingStatusText(t('checkout.waitingForPayment', 'Waiting for payment confirmation...'), 'pending');
    if (checkoutResult) {
      checkoutResult.textContent = t('checkout.paymentTabOpened', 'Payment tab opened. Complete payment and return to this tab.');
    }

    if (waitingAmount && redirectAmount) {
      waitingAmount.textContent = redirectAmount.textContent || '$0.00';
    }
    if (waitingCoupon && redirectCoupon) {
      waitingCoupon.textContent = redirectCoupon.textContent || t('checkout.summary.none', 'none');
    }
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
    if (event.key !== 'Escape') {
      return;
    }

    if (redirectModal && !redirectModal.classList.contains('hidden')) {
      closeRedirectModal();
    }

    if (waitingModal && !waitingModal.classList.contains('hidden')) {
      closeWaitingModal();
    }
  });

  if (waitingCloseButton) {
    waitingCloseButton.addEventListener('click', () => {
      closeWaitingModal();
    });
  }

  if (waitingCloseIconButton) {
    waitingCloseIconButton.addEventListener('click', () => {
      closeWaitingModal();
    });
  }

  if (waitingCheckButton) {
    waitingCheckButton.addEventListener('click', async () => {
      await refreshPendingOrderStatus(false);
    });
  }

  if (waitingModal) {
    waitingModal.addEventListener('click', (event) => {
      if (event.target === waitingModal) {
        closeWaitingModal();
      }
    });
  }

  window.addEventListener('focus', async () => {
    if (!pendingOrderId || !waitingModal || waitingModal.classList.contains('hidden')) {
      return;
    }

    await refreshPendingOrderStatus(true);
  });

  document.addEventListener('visibilitychange', async () => {
    if (document.visibilityState !== 'visible') {
      return;
    }

    if (!pendingOrderId || !waitingModal || waitingModal.classList.contains('hidden')) {
      return;
    }

    await refreshPendingOrderStatus(true);
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
      checkoutResult.textContent = t('checkout.creatingIntent', 'Creating payment intent...');
      try {
        const couponCode = appliedCouponCode || getCouponInputValue();
        const response = await fetch('/app/api/checkout/create-intent', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ couponCode: couponCode || null })
        });
        const payload = await response.json();
        if (!response.ok) {
          checkoutResult.textContent = payload.message || t('checkout.createIntentFailed', 'Create intent failed.');
          return;
        }

        applyQuoteToUi(payload);

        const intentId = payload.intentId;
        if (!intentId) {
          checkoutResult.textContent = t('checkout.intentMissing', 'Create intent succeeded but intent id is missing.');
          return;
        }

        redirectTargetUrl = `https://gate.paykrypt.io/pay/${encodeURIComponent(intentId)}`;
        pendingOrderId = payload.orderId || '';
        remainingSeconds = 10;

        if (redirectAmount) {
          redirectAmount.textContent = `$${formatMoney(payload.finalAmount ?? payload.amount ?? 0)}`;
        }

        if (redirectCoupon) {
          redirectCoupon.textContent = payload.appliedCouponCode
            ? `${payload.appliedCouponCode}${payload.appliedDiscountPercent !== null && payload.appliedDiscountPercent !== undefined ? ` (${payload.appliedDiscountPercent}%)` : ''}`
            : t('checkout.summary.none', 'none');
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
        checkoutResult.textContent = t('server.failed', 'Failed to contact server.');
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
      target.textContent = t('order.refreshing', 'Refreshing order...');
      try {
        const response = await fetch(`/app/api/checkout/${orderId}/status?refresh=true`);
        if (!response.ok) {
          target.textContent = t('order.loadFailed', 'Unable to load order status.');
          return;
        }

        const payload = await response.json();
        target.textContent = payload.licenseKey
          ? formatTemplate(
              t('order.paidIssued', 'Paid. License issued: {licenseKey}'),
              { licenseKey: payload.licenseKey }
            )
          : formatTemplate(
              t('order.statusTemplate', 'Order status: {orderStatus}, intent: {intentStatus}'),
              {
                orderStatus: payload.orderStatus ?? '-',
                intentStatus: payload.intentStatus ?? '-'
              }
            );
      } catch {
        target.textContent = t('order.refreshFailed', 'Refresh failed.');
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
      button.textContent = t('copy.copied', 'Copied');
      setTimeout(() => {
        button.textContent = t('copy.copy', 'Copy');
      }, 1200);
    });
  });
})();
