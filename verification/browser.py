"""Run against a local app; requires Python Playwright and Chromium."""
import os
from pathlib import Path
from playwright.sync_api import sync_playwright

base = os.environ.get("LAB_URL", "http://127.0.0.1:5012")
output = Path(__file__).parent
with sync_playwright() as p:
    browser = p.chromium.launch()
    page = browser.new_page(viewport={"width": 1440, "height": 1100})
    errors = []
    page.on("pageerror", lambda error: errors.append(str(error)))
    page.goto(base)
    page.wait_for_function("!document.querySelector('#run')?.disabled")
    page.get_by_role("heading", name="Recheck and finish cleanup", exact=True).wait_for()
    page.locator("#refresh-cleanup").click()
    # Inspect recovery controls without targeting any real account session.
    assert page.locator("#cleanup-session").count() or page.locator("#no-pending-cleanup").count()
    assert page.locator("#live-scenario option").count() == 3
    page.locator("#live-scenario").select_option("defer-archive")
    page.get_by_text("This is not an archive failure.", exact=False).wait_for()
    page.locator("#live-scenario").select_option("short-cleanup-window")
    page.get_by_text("A fast stop may still succeed", exact=False).wait_for()
    # Live buttons are intentionally not clicked; browser verification creates no provider resources.
    page.locator("#run").click()
    page.locator("#outcome").filter(has_text="passed").wait_for()
    for index, expected in [("1", "unconfirmed"), ("2", "paused")]:
        page.locator("#scenario").select_option(index)
        page.locator("#run").click()
        page.wait_for_function("(value) => document.querySelector('#outcome')?.textContent === value", arg=expected)
    page.locator("#statuses").fill('["terminated"]')
    page.locator("#run").click()
    page.wait_for_function("document.querySelector('#evidence')?.textContent.includes('not-needed')")
    page.locator("#statuses").fill("invalid json")
    page.locator("#run").click()
    page.locator("#status").filter(has_text="valid JSON array").wait_for()
    page.locator("[data-theme-toggle]").click()
    page.wait_for_function("document.documentElement.dataset.theme === 'dark'")
    page.screenshot(path=str(output / "desktop.png"), full_page=True)
    page.get_by_role("link", name="What you will learn", exact=True).click()
    page.locator("#learning").wait_for()
    page.get_by_role("link", name="Architecture & workflow", exact=True).click()
    page.get_by_role("heading", name="Actual C# source").wait_for()
    page.get_by_role("link", name="Application lab", exact=True).click()
    page.wait_for_function("!document.querySelector('#run')?.disabled")
    assert "running" in page.locator("#statuses").input_value()
    page.wait_for_function("document.querySelector('[data-theme-toggle]')?.getAttribute('aria-pressed') === 'true'")
    page.set_viewport_size({"width": 390, "height": 844})
    assert page.evaluate("document.documentElement.scrollWidth <= innerWidth"), "Mobile horizontal overflow"
    page.screenshot(path=str(output / "mobile.png"), full_page=True)
    assert not errors, errors
    browser.close()
print("PASS: Blazor interaction, scenarios, edited input, malformed JSON, theme, navigation and mobile layout")

