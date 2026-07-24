from selenium import webdriver
from selenium.webdriver.common.by import By
from selenium.webdriver.support.ui import WebDriverWait
from selenium.webdriver.support import expected_conditions as EC
import time

options = webdriver.ChromeOptions()
options.add_argument('--headless')
options.add_argument('--disable-gpu')
options.add_argument('--window-size=1280,1024')
driver = webdriver.Chrome(options=options)
driver.get('https://www.88jin88.com/?r=tbk8366')

try:
    WebDriverWait(driver, 15).until(EC.presence_of_element_located((By.TAG_NAME, 'input')))
    time.sleep(2)
    driver.save_screenshot('jin88_form.png')
except Exception as e:
    print('Wait failed:', e)

driver.quit()
